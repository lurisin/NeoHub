using System.Text.Json;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoHub.Services;
using NeoHub.Services.Models;
using static NeoHub.Tests.PanelNotificationParserTests;

namespace NeoHub.Tests.Infrastructure;

/// <summary>
/// Drives <see cref="PartitionConditionsService"/> step by step, without a panel connection or timing: status reads are
/// a request (<see cref="Request"/>) and an answer (<see cref="Answer"/>), notifications go through MediatR to the real
/// handlers, and the frames are those of the real WebSocket handler's recording client.
/// </summary>
internal sealed class ConditionsScenario : IAsyncDisposable
{
    public const string Id = TestHost.SessionId;

    public const string TamperOn = "01-12-01-81-01-01-01";
    public const string TamperOff = "01-12-01-81-01-01-00";
    public const string AcOn = "01-00-01-09-01-00-01";
    public const string AcOff = "01-00-01-09-01-00-00";
    public const string BatteryOn = "01-00-01-87-01-00-01";
    public const string BatteryOff = "01-00-01-87-01-00-00";

    private int _rereads;
    private int _mark;

    private ConditionsScenario(TestHost host)
    {
        Host = host;
        Conditions = host.Services.GetRequiredService<PartitionConditionsService>();
        Mediator = host.Services.GetRequiredService<IMediator>();
        Log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Scenario");
        Conditions.TroubleRereadRequested += (_, _) => Interlocked.Increment(ref _rereads);
    }

    public TestHost Host { get; }
    public PartitionConditionsService Conditions { get; }
    public IMediator Mediator { get; }
    public ILogger Log { get; }
    public SessionState Session => Host.State.GetSession(Id)!;
    public int Rereads => Volatile.Read(ref _rereads);

    /// <summary>A session with these partitions (disarmed, ready), trouble refresh interval as given (0 = off).</summary>
    public static async Task<ConditionsScenario> StartAsync(int interval = 60, int partitions = 2)
    {
        var host = await TestHost.StartAsync(interval);
        for (byte p = 1; p <= partitions; p++)
            host.State.UpdatePartition(Id, new PartitionState { PartitionNumber = p, Status = PartitionStatus.Disarmed, IsReady = true });
        var scenario = new ConditionsScenario(host);
        scenario.Mark();
        return scenario;
    }

    public long Request() => Conditions.BeginStatusRead(Session);

    public void Answer(long requestedAt, byte partition, bool troubles, StatusReadKind kind = StatusReadKind.Refresh) =>
        AnswerBits(requestedAt, partition, Status2(troubles), kind);

    /// <summary>A status read answer with these PartitionStatus2 bits.</summary>
    public void AnswerBits(long requestedAt, byte partition, ModulePartitionStatus.PartitionStatus2 bits, StatusReadKind kind = StatusReadKind.Refresh)
    {
        var state = Session.Partitions[partition];
        Conditions.ApplyStatusRead(Id, Session, state, bits, requestedAt, kind, Log);
        if (kind == StatusReadKind.ConnectPull)
            Host.State.UpdatePartition(Id, state);   // the pull announces the partitions it read
    }

    public void ReadBits(byte partition, ModulePartitionStatus.PartitionStatus2 bits) => AnswerBits(Request(), partition, bits);

    public const ModulePartitionStatus.PartitionStatus2 SirenBit = ModulePartitionStatus.PartitionStatus2.AudibleBellSiren;
    public const ModulePartitionStatus.PartitionStatus2 MemoryBit = ModulePartitionStatus.PartitionStatus2.AlarmsInMemory;

    public void Read(byte partition, bool troubles) => Answer(Request(), partition, troubles);

    public void PullStarted() => Conditions.ConnectPullStarted(Session);

    public void PullDone() => Conditions.ConnectPullStatusDone(Id, Session, Log);

    /// <summary>A whole connect pull: one read per partition, in order, then the end of the status step.</summary>
    public void Pull(params bool[] troubles)
    {
        PullStarted();
        for (int i = 0; i < troubles.Length; i++)
            Answer(Request(), (byte)(i + 1), troubles[i], StatusReadKind.ConnectPull);
        PullDone();
    }

    public Task Detail(string payload, bool burst = false) =>
        Mediator.Publish(new SessionNotification<TroubleDetailNotification>(
            Id, new TroubleDetailNotification { Payload = Hex(payload), ReceivedInConnectBurst = burst }, DateTime.UtcNow));

    public Task Bell(string payload) =>
        Mediator.Publish(new SessionNotification<PartitionAudibleBellNotification>(
            Id, new PartitionAudibleBellNotification { Payload = Hex(payload) }, DateTime.UtcNow));

    public Task Memory(string payload) =>
        Mediator.Publish(new SessionNotification<PartitionAlarmMemoryNotification>(
            Id, new PartitionAlarmMemoryNotification { Payload = Hex(payload) }, DateTime.UtcNow));

    public Task ArmDisarm(byte partition, DSC.TLink.ITv2.Enumerations.ArmingMode mode) =>
        Mediator.Publish(new SessionNotification<NotificationArmDisarm>(
            Id, new NotificationArmDisarm { Partition = partition, ArmMode = mode, UserId = 1 }, DateTime.UtcNow));

    /// <summary>Siren and alarm memory of a partition, as "siren:on memory:off".</summary>
    public string SirenMemoryOf(byte partition)
    {
        var p = Session.Partitions[partition];
        return $"siren:{(p.SirenActive ? "on" : "off")} memory:{(p.AlarmsInMemory ? "on" : "off")}";
    }

    /// <summary>The partition_update frames since <see cref="Mark"/>, as "p1 disarmed siren:on memory:off", and starts a new step.</summary>
    public List<string> SirenMemoryUpdates()
    {
        var frames = Host.Client.FramesSince(_mark);
        Mark();
        return frames.Select(f => JsonDocument.Parse(f).RootElement)
            .Where(r => r.GetProperty("type").GetString() == "partition_update")
            .Select(r => $"p{r.GetProperty("partition_number").GetInt32()} {r.GetProperty("status").GetString()}"
                + $" siren:{(r.GetProperty("siren").GetBoolean() ? "on" : "off")}"
                + $" memory:{(r.GetProperty("alarm_memory").GetBoolean() ? "on" : "off")}")
            .ToList();
    }

    public static ModulePartitionStatus.PartitionStatus2 Status2(bool troubles) =>
        troubles ? ModulePartitionStatus.PartitionStatus2.TroublesPresent : 0;

    /// <summary>Troubles flag and trouble types of a partition, as "on:a,b" or "off:".</summary>
    public string StateOf(byte partition)
    {
        var p = Session.Partitions[partition];
        return (p.TroublesPresent ? "on:" : "off:") + string.Join(",", p.TroubleTypes);
    }

    /// <summary>Starts a new step: <see cref="Updates"/> returns what was sent after this.</summary>
    public void Mark() => _mark = Host.Client.Count;

    /// <summary>The partition_update frames since <see cref="Mark"/>, as "p1 on:a,b", and starts a new step.</summary>
    public List<string> Updates()
    {
        var frames = Host.Client.FramesSince(_mark);
        Mark();
        return frames.Select(f => JsonDocument.Parse(f).RootElement)
            .Where(r => r.GetProperty("type").GetString() == "partition_update")
            .Select(r => $"p{r.GetProperty("partition_number").GetInt32()} "
                + (r.GetProperty("troubles").GetBoolean() ? "on:" : "off:")
                + string.Join(",", r.GetProperty("trouble_types").EnumerateArray().Select(t => t.GetString())))
            .ToList();
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
