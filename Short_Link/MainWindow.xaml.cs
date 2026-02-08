using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Short_Link
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Хранилище соответствий: token -> originalUrl
        private readonly ConcurrentDictionary<string, string> _store = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private int _listeningPort;

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var input = AddLink.Text?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                MessageBox.Show("Введите ссылку.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var normalized = NormalizeUrl(input);
            if (normalized == null)
            {
                MessageBox.Show("Неверный URL.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Убедимся, что сервер запущен
            try
            {
                await EnsureServerRunningAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Не удалось запустить локальный сервер: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                return;
            }

            // Генерация уникального токена
            string token;
            do
            {
                token = GenerateToken(6);
            } while (!_store.TryAdd(token, normalized)); // повторяем при коллизии

            var shortUrl = $"http://localhost:{_listeningPort}/wnk/{token}";

            Dispatcher.Invoke(() => CopyLink.Text = shortUrl);
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            var text = CopyLink.Text;
            if (string.IsNullOrEmpty(text))
            {
                MessageBox.Show("Нет ссылки для копирования.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Clipboard.SetText(text);
                MessageBox.Show("Ссылка скопирована в буфер обмена.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось копировать: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Нормализация URL: если схема отсутствует, подставляется http://. Возвращает null при невалидном URL.
        private static string NormalizeUrl(string input)
        {
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.AbsoluteUri;
            }

            if (Uri.TryCreate("http://" + input, UriKind.Absolute, out uri))
            {
                return uri.AbsoluteUri;
            }

            return null;
        }

        // Генерация URL-токена из [A-Za-z0-9], криптографически безопасно
        private static string GenerateToken(int length)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var data = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(data);
            }

            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                var idx = data[i] % chars.Length;
                sb.Append(chars[idx]);
            }

            return sb.ToString();
        }

        // Запускает HttpListener на свободном порту (если ещё не запущен)
        private Task EnsureServerRunningAsync()
        {
            if (_listener != null && _listener.IsListening)
                return Task.CompletedTask;

            _cts = new CancellationTokenSource();
            _listeningPort = GetFreePort();
            _listener = new HttpListener();
            var prefix = $"http://localhost:{_listeningPort}/";
            _listener.Prefixes.Add(prefix);
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                _listener = null;
                _cts.Cancel();
                throw new InvalidOperationException($"Не удалось запустить HttpListener на {prefix}: {ex.Message}", ex);
            }

            Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);

            Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"Локальный сервер запущен на {prefix}\nСокращённые ссылки будут иметь вид: {prefix}wnk/{{token}}", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            });

            return Task.CompletedTask;
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
                {
                    HttpListenerContext context = null;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    _ = Task.Run(() => HandleRequestAsync(context), token);
                }
            }
            catch
            {
                // ignore
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                var req = context.Request;
                var resp = context.Response;

                var raw = req.Url.AbsolutePath.Trim('/');
                var parts = raw.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && string.Equals(parts[0], "wnk", StringComparison.OrdinalIgnoreCase))
                {
                    var token = parts[1];
                    if (_store.TryGetValue(token, out var original))
                    {
                        resp.StatusCode = (int)HttpStatusCode.Found;
                        resp.RedirectLocation = original;
                        resp.Close();
                        return;
                    }
                }

                var body = $"<html><body><h2>Short Link — Not Found</h2><p>Requested: {WebUtility.HtmlEncode(req.Url.AbsolutePath)}</p></body></html>";
                var buffer = Encoding.UTF8.GetBytes(body);
                resp.ContentType = "text/html; charset=utf-8";
                resp.StatusCode = (int)HttpStatusCode.NotFound;
                resp.ContentLength64 = buffer.Length;
                await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                resp.Close();
            }
            catch
            {
                try { context?.Response.Close(); } catch { }
            }
        }

        // Получение свободного порта
        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        // Остановим сервер при закрытии окна
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            try
            {
                _cts?.Cancel();
                if (_listener != null)
                {
                    try { _listener.Stop(); } catch { }
                    try { _listener.Close(); } catch { }
                    _listener = null;
                }
            }
            catch { }
        }
    }
}