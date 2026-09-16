using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HyprNetShell.Core.Features.OnlineAccounts;

internal sealed class OAuthCallbackListener : IAsyncDisposable
{
    private const string CallbackPath = "/auth/callback";

    private readonly TcpListener _listener;
    private readonly string _callbackHost;
    private bool _disposed;

    private OAuthCallbackListener(TcpListener listener, string callbackHost)
    {
        _listener = listener;
        _callbackHost = callbackHost;
    }

    internal string RedirectUri
    {
        get
        {
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            return $"http://{_callbackHost}:{endpoint.Port}{CallbackPath}";
        }
    }

    internal static Task<OAuthCallbackListener> StartAsync(
        IReadOnlyList<int> fixedPorts,
        string callbackHost,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fixedPorts.Count == 0)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            return Task.FromResult(new OAuthCallbackListener(listener, callbackHost));
        }

        foreach (var port in fixedPorts)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start(1);
                return Task.FromResult(new OAuthCallbackListener(listener, callbackHost));
            }
            catch (SocketException)
            {
                // Try the provider's next supported callback port.
            }
        }

        throw new InvalidOperationException(
            $"OAuth callback ports {string.Join(" and ", fixedPorts)} are already in use.");
    }

    internal async Task<string> WaitForCodeAsync(
        string expectedState,
        Action callbackReceived,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            if (requestLine is null)
            {
                continue;
            }

            string? line;
            do
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            while (!string.IsNullOrEmpty(line));

            var target = ParseTarget(requestLine);
            if (target is null || !string.Equals(target.AbsolutePath, CallbackPath, StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, HttpStatusCode.NotFound, "Sign-in callback not found.", cancellationToken);
                continue;
            }

            callbackReceived();
            var query = ParseQuery(target.Query);
            if (!query.TryGetValue("state", out var state) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expectedState),
                    Encoding.UTF8.GetBytes(state)))
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "The sign-in state did not match. You can close this page.", cancellationToken);
                throw new InvalidOperationException("The OAuth callback state did not match.");
            }

            if (query.TryGetValue("error", out var error))
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "Sign-in was cancelled. You can close this page.", cancellationToken);
                throw new InvalidOperationException($"The provider cancelled sign-in ({error}).");
            }

            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "No authorization code was returned. You can close this page.", cancellationToken);
                throw new InvalidOperationException("The provider did not return an authorization code.");
            }

            await WriteResponseAsync(stream, HttpStatusCode.OK, "Sign-in received. You can close this page and return to HyprNetShell.", cancellationToken);
            return code;
        }
    }

    private static Uri? ParseTarget(string requestLine)
    {
        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? "" : part[(separator + 1)..];
            result[DecodeQueryComponent(key)] = DecodeQueryComponent(value);
        }

        return result;
    }

    private static string DecodeQueryComponent(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpStatusCode status,
        string message,
        CancellationToken cancellationToken)
    {
        var body = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>HyprNetShell sign-in</title></head><body><h1>HyprNetShell</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _listener.Stop();
        }

        return ValueTask.CompletedTask;
    }
}
