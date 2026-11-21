using System.Net.WebSockets;
using System.Text;

namespace NeoHub.Tests.Infrastructure;

/// <summary>A WebSocket client stand-in that records every text frame the server sends to it.</summary>
public sealed class CaptureSocket : WebSocket
{
    private readonly List<string> _frames = new();

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Dispose() { }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        lock (_frames)
            _frames.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        return Task.CompletedTask;
    }

    public int Count { get { lock (_frames) return _frames.Count; } }
    public List<string> Frames() { lock (_frames) return _frames.ToList(); }
    public List<string> FramesSince(int index) { lock (_frames) return _frames.Skip(index).ToList(); }
}
