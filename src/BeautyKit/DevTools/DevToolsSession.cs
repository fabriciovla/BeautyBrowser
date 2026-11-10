using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CefSharp;
using CefSharp.Callback;
using Microsoft.UI.Dispatching;

namespace BeautyKit.DevTools;

/// <summary>
/// DevTools shown inside the app. The frontend is another offscreen <see cref="BrowserTab"/> connected over WebSocket
/// to <see cref="DevToolsBridge"/>, which relays the protocol (CDP) to the inspected page.
/// </summary>
public sealed class DevToolsSession : IDisposable
{
    // Our own message ids, so they never clash with the frontend's.
    private const int FrontendIdBase = 1_000_000;
    private const int InspectIdBase = 2_000_000;

    private readonly DevToolsBridge _bridge;
    private readonly DispatcherQueue _ui;
    private readonly Dictionary<int, int> _frontendIds = [];  // id sent to the engine → the frontend's original id
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private IRegistration? _observer;
    private int _nextFrontendId = FrontendIdBase;
    private int _nextInspectId = InspectIdBase;
    private (int X, int Y)? _pendingInspect;
    private bool _connected;
    private bool _disposed;

    internal DevToolsSession(BrowserEngine engine, BrowserTab inspected, DevToolsBridge bridge, DispatcherQueue ui, int x, int y)
    {
        _bridge = bridge;
        _ui = ui;
        Inspected = inspected;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _pendingInspect = (x, y);

        bridge.Register(this);
        Frontend = engine.CreateTab($"devtools://devtools/bundled/devtools_app.html?ws=127.0.0.1:{bridge.Port}/{Token}");
    }

    public BrowserTab Inspected { get; }

    public BrowserTab Frontend { get; }

    internal string Token { get; }

    /// <summary>Reveals the element at (x, y) of the page in the Elements panel.</summary>
    public void InspectAt(int x, int y)
    {
        if (_connected) SendInspect(x, y);
        else _pendingInspect = (x, y);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bridge.Unregister(this);
        _observer?.Dispose();
        _outgoing.Writer.TryComplete();
        Frontend.Dispose();
    }

    // The connection runs on a pool thread; everything that touches CEF goes back to the UI thread.

    internal async Task RunAsync(WebSocket socket, CancellationToken stop)
    {
        _ui.TryEnqueue(OnConnected);
        var sender = SendLoopAsync(socket, stop);

        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, stop);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            _ui.TryEnqueue(() => FromFrontend(text));
        }

        _ui.TryEnqueue(() => _connected = false);
        await sender.WaitAsync(TimeSpan.FromSeconds(1)).ContinueWith(_ => { });
    }

    private async Task SendLoopAsync(WebSocket socket, CancellationToken stop)
    {
        await foreach (var text in _outgoing.Reader.ReadAllAsync(stop))
        {
            if (socket.State != WebSocketState.Open) break;
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, stop);
        }
    }

    private void OnConnected()
    {
        if (_disposed) return;
        _connected = true;
        _observer ??= Inspected.DevToolsHost()?.AddDevToolsMessageObserver(new Observer(this));

        // Give the frontend time to load the document before highlighting the element.
        if (_pendingInspect is { } point)
        {
            _pendingInspect = null;
            _ = Task.Delay(1500).ContinueWith(_ => _ui.TryEnqueue(() => SendInspect(point.X, point.Y)));
        }
    }


    private void FromFrontend(string json)
    {
        if (_disposed || JsonNode.Parse(json) is not JsonObject message) return;

        if (message["id"]?.GetValue<int>() is { } id)
        {
            var mapped = _nextFrontendId++;
            _frontendIds[mapped] = id;
            message["id"] = mapped;
        }
        Inspected.DevToolsHost()?.SendDevToolsMessage(message.ToJsonString());
    }


    private void FromBackend(string json)
    {
        if (_disposed || JsonNode.Parse(json) is not JsonObject message) return;

        if (message["id"]?.GetValue<int>() is not { } id)
        {
            _outgoing.Writer.TryWrite(json); // an event: forwarded as is
            return;
        }

        if (_frontendIds.Remove(id, out var original))
        {
            message["id"] = original;
            _outgoing.Writer.TryWrite(message.ToJsonString());
        }
        else if (id >= InspectIdBase && message["result"]?["backendNodeId"]?.GetValue<int>() is { } nodeId)
        {
            // The same event Chromium sends when an element is picked with the inspector.
            _outgoing.Writer.TryWrite(new JsonObject
            {
                ["method"] = "Overlay.inspectNodeRequested",
                ["params"] = new JsonObject { ["backendNodeId"] = nodeId },
            }.ToJsonString());
        }
        // Other responses belong to other protocol clients.
    }

    private void SendInspect(int x, int y)
    {
        var request = new JsonObject
        {
            ["id"] = _nextInspectId++,
            ["method"] = "DOM.getNodeForLocation",
            ["params"] = new JsonObject { ["x"] = x, ["y"] = y, ["includeUserAgentShadowDOM"] = false },
        };
        Inspected.DevToolsHost()?.SendDevToolsMessage(request.ToJsonString());
    }

    private sealed class Observer(DevToolsSession session) : IDevToolsMessageObserver
    {
        public bool OnDevToolsMessage(IBrowser browser, Stream message)
        {
            using var reader = new StreamReader(message, Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (session._ui.HasThreadAccess) session.FromBackend(text);
            else session._ui.TryEnqueue(() => session.FromBackend(text));
            return true;
        }

        public void OnDevToolsMethodResult(IBrowser browser, int messageId, bool success, Stream result) { }
        public void OnDevToolsEvent(IBrowser browser, string method, Stream parameters) { }
        public void OnDevToolsAgentAttached(IBrowser browser) { }
        public void OnDevToolsAgentDetached(IBrowser browser) { }
        public void Dispose() { }
    }
}
