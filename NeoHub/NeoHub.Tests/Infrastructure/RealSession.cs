using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DSC.TLink;
using DSC.TLink.Extensions;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NeoHub.Tests.Infrastructure;

/// <summary>
/// A transport that records every payload the session sends (plaintext: these sessions have no encryption
/// handler) and delivers inbound packets fed by the test.
/// </summary>
internal sealed class FakeTransport : ITLinkTransport
{
    private readonly Channel<Result<TLinkMessage>> _inbound = Channel.CreateUnbounded<Result<TLinkMessage>>();
    private readonly ConcurrentQueue<byte[]> _sent = new();
    private byte _senderSequence = 100;

    /// <summary>Called for every outbound packet, decoded.</summary>
    public Action<ITv2Packet>? OnSend { get; set; }

    public bool Disposed { get; private set; }
    public ReadOnlyMemory<byte> DefaultHeader => ReadOnlyMemory<byte>.Empty;

    public IReadOnlyList<byte[]> SentPayloads => _sent.ToArray();

    /// <summary>The decoded messages of every outbound packet, in order (acks included).</summary>
    public List<IMessageData> SentMessages() => _sent.Select(Decode).Select(p => p.Message).ToList();

    public Task<Result<TLinkMessage>> ReadMessageAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("handshake is not simulated");

    public async IAsyncEnumerable<Result<TLinkMessage>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _inbound.Reader.ReadAllAsync(cancellationToken))
            yield return item;
    }

    public Task<Result> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (Disposed)
            return Task.FromResult(Result.Fail(TLinkErrorCode.Disconnected, "transport disposed"));
        var bytes = payload.ToArray();
        _sent.Enqueue(bytes);
        OnSend?.Invoke(Decode(bytes));
        return Task.FromResult(Result.Ok());
    }

    public Task<Result> SendAsync(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        SendAsync(payload, cancellationToken);

    /// <summary>Delivers a packet from the panel.</summary>
    public void Feed(byte receiverSequence, IMessageData message)
    {
        var bytes = new List<byte> { _senderSequence++, receiverSequence };
        bytes.AddRange(MessageFactory.SerializeMessage(message));
        ITv2Framing.AddFraming(bytes);
        _inbound.Writer.TryWrite(Result<TLinkMessage>.Ok(new TLinkMessage(ReadOnlyMemory<byte>.Empty, bytes.ToArray())));
    }

    /// <summary>The panel closes the connection: the inbound stream ends.</summary>
    public void EndInbound() => _inbound.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public static ITv2Packet Decode(byte[] framed)
    {
        var span = new ReadOnlySpan<byte>(framed);
        ITv2Framing.RemoveFraming(ref span);
        var sender = span.PopByte();
        var receiver = span.PopByte();
        return new ITv2Packet(sender, receiver, MessageFactory.DeserializeMessage(span));
    }

    /// <summary>True when the framed payload carries the given command word (0x0908 for FAP, for example).</summary>
    public static bool CarriesCommandWord(byte[] framed, ushort commandWord)
    {
        var span = new ReadOnlySpan<byte>(framed);
        ITv2Framing.RemoveFraming(ref span);
        return span.Length >= 4 && span[2] == (byte)(commandWord >> 8) && span[3] == (byte)commandWord;
    }
}

/// <summary>
/// A real <see cref="ITv2Session"/> on a <see cref="FakeTransport"/>, created without the handshake (no
/// encryption) and with its receive pump started, as ITv2Session.CreateAsync does after the handshake.
/// </summary>
internal sealed class RealSession
{
    private static readonly ConstructorInfo Ctor = typeof(ITv2Session)
        .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
    private static readonly MethodInfo Pump = typeof(ITv2Session)
        .GetMethod("ReceivePumpAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly PropertyInfo SessionIdProperty = typeof(ITv2Session).GetProperty(nameof(ITv2Session.SessionId))!;

    private RealSession(ITv2Session session, FakeTransport transport, Task pump)
    {
        Session = session;
        Transport = transport;
        PumpTask = pump;
    }

    public ITv2Session Session { get; }
    public FakeTransport Transport { get; }
    public Task PumpTask { get; }

    /// <summary>Creates the session and waits until it is ready (2 s after the pump started with no traffic).</summary>
    public static async Task<RealSession> StartAsync(string sessionId, ILoggerFactory? loggers = null, bool waitUntilReady = true)
    {
        var transport = new FakeTransport();
        var logger = (loggers ?? NullLoggerFactory.Instance).CreateLogger<ITv2Session>();
        var session = (ITv2Session)Ctor.Invoke(new object[] { transport, new NoSettings(), logger });
        SessionIdProperty.SetValue(session, sessionId);
        var pump = (Task)Pump.Invoke(session, null)!;
        var real = new RealSession(session, transport, pump);
        if (waitUntilReady)
            await real.WaitReadyAsync();
        return real;
    }

    public async Task WaitReadyAsync()
    {
        var ready = (TaskCompletionSource)typeof(ITv2Session)
            .GetField("_sessionReady", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Session)!;
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class NoSettings : IConnectionSettingsProvider
    {
        public ConnectionSettings? ResolveConnection(string sessionId, DSC.TLink.ITv2.Enumerations.EncryptionType encryptionType) => null;
        public void ConfirmDefaults(string sessionId) { }
    }
}
