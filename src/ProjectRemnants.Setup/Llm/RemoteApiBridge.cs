using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ProjectRemnants.Setup.Llm;

public sealed class RemoteApiBridge : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(95) };
    private HttpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;
    private string _remoteEndpoint = string.Empty;
    private string _remoteApiKey = string.Empty;
    private string _localToken = string.Empty;

    public string Endpoint { get; private set; } = string.Empty;
    public string LocalToken => _localToken;
    public bool IsRunning => _listener?.IsListening == true;

    public Task StartAsync(string remoteEndpoint, string remoteApiKey)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("The API bridge is already running.");
        }

        _remoteEndpoint = NormalizeEndpoint(remoteEndpoint);
        _remoteApiKey = RequireText(remoteApiKey, "API key");
        _localToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        for (var port = 20129; port <= 20139; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Endpoint = $"http://127.0.0.1:{port}/v1/chat/completions";
                _lifetime = new CancellationTokenSource();
                _acceptLoop = AcceptLoopAsync(listener, _lifetime.Token);
                return Task.CompletedTask;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("No local API bridge port is available.");
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        var lifetime = _lifetime;
        var loop = _acceptLoop;
        _listener = null;
        _lifetime = null;
        _acceptLoop = null;
        Endpoint = string.Empty;

        if (lifetime is not null)
        {
            lifetime.Cancel();
        }

        if (listener is not null)
        {
            listener.Close();
        }

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpListenerException)
            {
            }
        }

        lifetime?.Dispose();
        _remoteApiKey = string.Empty;
        _localToken = string.Empty;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            _ = HandleAsync(context, cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.HttpMethod != "POST" ||
                context.Request.Url?.AbsolutePath != "/v1/chat/completions")
            {
                await WriteErrorAsync(context.Response, 404, "Not found.", cancellationToken);
                return;
            }

            if (!HasValidToken(context.Request.Headers["Authorization"]))
            {
                await WriteErrorAsync(context.Response, 401, "Unauthorized.", cancellationToken);
                return;
            }

            if (context.Request.ContentLength64 > 1_048_576)
            {
                await WriteErrorAsync(context.Response, 413, "Request too large.", cancellationToken);
                return;
            }

            using var upstream = new HttpRequestMessage(HttpMethod.Post, _remoteEndpoint);
            upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _remoteApiKey);
            upstream.Content = new StreamContent(context.Request.InputStream);
            upstream.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(
                upstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString()
                ?? "application/json";
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            await body.CopyToAsync(context.Response.OutputStream, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (!context.Response.OutputStream.CanWrite)
            {
                return;
            }

            try
            {
                await WriteErrorAsync(
                    context.Response, 502, "The upstream API is unavailable.", cancellationToken);
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private bool HasValidToken(string? authorization)
    {
        var expected = $"Bearer {_localToken}";
        if (authorization is null || authorization.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(authorization),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }

    private static async Task WriteErrorAsync(
        HttpListenerResponse response, int status, string message,
        CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    private static string NormalizeEndpoint(string value)
    {
        value = RequireText(value, "endpoint").TrimEnd('/');
        if (!value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            value += "/chat/completions";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 ||
            uri.Scheme != Uri.UriSchemeHttps &&
            !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw new ArgumentException("Use an HTTPS endpoint or loopback HTTP endpoint.");
        }

        return uri.AbsoluteUri;
    }

    private static string RequireText(string value, string name)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            throw new ArgumentException($"Enter an {name}.");
        }

        return value;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
