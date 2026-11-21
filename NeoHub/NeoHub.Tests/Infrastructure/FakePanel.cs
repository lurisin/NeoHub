using System.Collections.Concurrent;
using System.Threading.Channels;
using DSC.TLink;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;

namespace NeoHub.Tests.Infrastructure;

/// <summary>
/// A simulated panel behind <see cref="FakeSession"/>: answers the connect pull (capabilities, labels, partition
/// and zone status) and commands, and records every message it was sent.
/// </summary>
internal sealed class FakePanel
{
    public int MaxZones { get; set; } = 3;
    public int MaxPartitions { get; set; } = 2;

    /// <summary>Status1 / Status2 bytes per partition for ModulePartitionStatus answers (0x02 = ready to arm).</summary>
    public ConcurrentDictionary<int, (byte S1, byte S2)> PartitionStatus { get; } =
        new(new Dictionary<int, (byte, byte)> { [1] = (0x02, 0x00), [2] = (0x02, 0x00) });

    /// <summary>CommandResponse code for each command type (default 0 = success).</summary>
    public ConcurrentDictionary<Type, byte> ResponseCodes { get; } = new();

    /// <summary>When set for a command type, the command is answered with a Command_Error carrying this NACK code.</summary>
    public ConcurrentDictionary<Type, byte> NackCodes { get; } = new();

    /// <summary>Command types the panel never answers (the sender waits until it gives up).</summary>
    public ConcurrentDictionary<Type, bool> NeverAnswer { get; } = new();

    /// <summary>
    /// Command types the panel answers with a transport-level SimpleAck only (behind a real session's transport),
    /// then nothing, or, when the value is a delay, then a CommandResponse in a separate packet after that delay.
    /// </summary>
    public ConcurrentDictionary<Type, TimeSpan?> AckFirst { get; } = new();

    /// <summary>While set, ModulePartitionStatus requests wait for it before they are answered.</summary>
    public TaskCompletionSource? HoldPartitionStatus { get; set; }

    /// <summary>While set, ModuleZoneStatus requests (the last step of the connect pull) wait for it.</summary>
    public TaskCompletionSource? HoldZoneStatus { get; set; }

    public ConcurrentQueue<IMessageData> Received { get; } = new();

    public async Task<Result<IMessageData>> HandleAsync(IMessageData message, CancellationToken ct)
    {
        Received.Enqueue(message);
        if (message is CommandRequestMessage request)
        {
            return request.Request switch
            {
                ConnectionSystemCapabilities => Ok(new ConnectionSystemCapabilities { MaxZones = MaxZones, MaxPartitions = MaxPartitions, MaxUsers = 8 }),
                NotificationLabelText labels => Ok(new NotificationLabelText { Collection = labels.Collection, Start = labels.Start, End = labels.End }),
                ModuleZoneStatus => await HoldThen(HoldZoneStatus, ct, () => new ModuleZoneStatus
                {
                    ZoneStart = 1,
                    ZoneCount = MaxZones,
                    StatusSizeInBytes = 1,
                    ZoneStatusBytes = new ModuleZoneStatus.ZoneStatusEnum[MaxZones]
                }),
                ModulePartitionStatus mps => await HoldThen(HoldPartitionStatus, ct, () => PartitionStatusAnswer(mps.Partition)),
                _ => throw new InvalidOperationException("unexpected request " + request.Request?.GetType().Name),
            };
        }

        var type = message.GetType();
        if (NeverAnswer.ContainsKey(type))
            await Task.Delay(Timeout.Infinite, ct);

        var sequence = (message as CommandMessageBase)?.CommandSequence ?? 0;
        if (NackCodes.TryGetValue(type, out var nack))
            return Ok(new CommandError { NackCommand = MessageFactory.GetCommand(message), NackCode = (ITv2NackCode)nack });

        var code = ResponseCodes.TryGetValue(type, out var c) ? c : (byte)0;
        return Ok(new CommandResponse { CommandSequence = sequence, ResponseCode = (CommandResponseCode)code });
    }

    public ModulePartitionStatus PartitionStatusAnswer(int partition)
    {
        var (s1, s2) = PartitionStatus.TryGetValue(partition, out var st) ? st : ((byte)0x02, (byte)0x00);
        return new ModulePartitionStatus { Partition = partition, PartitionStatus = new byte[] { s1, s2, 0x00 } };
    }

    public List<T> ReceivedOf<T>() => Received.OfType<T>().ToList();

    private static Result<IMessageData> Ok(IMessageData message) => Result<IMessageData>.Ok(message);

    private static async Task<Result<IMessageData>> HoldThen(TaskCompletionSource? hold, CancellationToken ct, Func<IMessageData> answer)
    {
        if (hold is not null)
            await hold.Task.WaitAsync(ct);
        return Ok(answer());
    }
}

/// <summary>
/// An <see cref="IITv2Session"/> stand-in for one panel connection. Notifications are fed with <see cref="Notify"/>;
/// <see cref="EndConnection"/> models the TCP connection going away (its notification stream ends).
/// </summary>
internal sealed class FakeSession : IITv2Session
{
    private readonly Channel<IMessageData> _notifications = Channel.CreateUnbounded<IMessageData>();
    private readonly CancellationTokenSource _closed = new();
    private int _disposeCount;
    private int _abandonCount;

    public FakeSession(string sessionId, FakePanel panel, string name)
    {
        SessionId = sessionId;
        Panel = panel;
        Name = name;
    }

    public string SessionId { get; }
    public string Name { get; }
    public FakePanel Panel { get; }

    /// <summary>
    /// A connection the panel no longer answers on (for example a stale TCP connection after the panel reconnected):
    /// every send waits until the session is closed or the caller gives up.
    /// </summary>
    public volatile bool Dead;

    /// <summary>
    /// When true, closing the session does not end its notification stream; it ends with <see cref="EndConnection"/>.
    /// Models a session whose receive pump still delivers what it had read before it was closed.
    /// </summary>
    public bool KeepStreamOpenOnClose { get; init; }

    /// <summary>Delay of DisposeAsync (a real session waits up to 2 s for END_SESSION to go out).</summary>
    public int DisposeDelayMs { get; set; }

    public ConcurrentQueue<IMessageData> Sent { get; } = new();
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int AbandonCount => Volatile.Read(ref _abandonCount);
    public bool IsClosed => _closed.IsCancellationRequested;

    public async Task<Result<IMessageData>> SendAsync(IMessageData message, CancellationToken ct = default)
    {
        Sent.Enqueue(message);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        if (Dead)
            await Task.Delay(Timeout.Infinite, linked.Token);
        return await Panel.HandleAsync(message, linked.Token);
    }

    public IAsyncEnumerable<IMessageData> GetNotificationsAsync(CancellationToken ct = default) =>
        _notifications.Reader.ReadAllAsync(ct);

    public void Notify(IMessageData message) => _notifications.Writer.TryWrite(message);

    public void EndConnection() => _notifications.Writer.TryComplete();

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        if (DisposeDelayMs > 0)
            await Task.Delay(DisposeDelayMs);
        Close();
    }

    /// <summary>Closes the session without END_SESSION (ITv2Session.AbandonAsync).</summary>
    public ValueTask AbandonAsync()
    {
        Interlocked.Increment(ref _abandonCount);
        Close();
        return ValueTask.CompletedTask;
    }

    private void Close()
    {
        try { _closed.Cancel(); } catch (ObjectDisposedException) { }
        if (!KeepStreamOpenOnClose)
            _notifications.Writer.TryComplete();
    }

    public override string ToString() => $"FakeSession {Name} ({SessionId})";
}
