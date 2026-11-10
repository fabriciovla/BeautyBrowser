using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace BeautyKit.DevTools;

/// <summary>
/// Minimal WebSocket server on 127.0.0.1 that connects the DevTools frontend (a regular page shown in the app) to the
/// inspected page.
/// </summary>
/// <remarks>
/// Chromium's remote debugging port is not used because any local process could drive the browser through it.
/// Instead, every session has a random 128-bit token in its path.
/// </remarks>
internal sealed class DevToolsBridge : IDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, DevToolsSession> _sessions = new();
    private readonly CancellationTokenSource _stop = new();

    public DevToolsBridge()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public int Port { get; }

    public void Register(DevToolsSession session) => _sessions[session.Token] = session;
    public void Unregister(DevToolsSession session) => _sessions.TryRemove(session.Token, out _);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var request = await ReadHeadersAsync(stream);
            if (request is null) return;

            // "GET /<token> HTTP/1.1"
            var lines = request.Split("\r\n");
            var parts = lines[0].Split(' ');
            var token = parts.Length > 1 ? parts[1].TrimStart('/') : "";
            var key = lines.Skip(1)
                .Select(l => l.Split(':', 2))
                .Where(p => p.Length == 2 && p[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[1].Trim())
                .FirstOrDefault();

            if (key is null || !_sessions.TryGetValue(token, out var session))
            {
                await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n"));

            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });
            await session.RunAsync(socket, _stop.Token);
        }
        catch
        {
            // Connection dropped; the DevTools frontend reconnects when it is reloaded.
        }
    }

    private static async Task<string?> ReadHeadersAsync(NetworkStream stream)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            buffer.Add(one[0]);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
                return Encoding.ASCII.GetString(buffer.ToArray());
        }
        return null;
    }
}
