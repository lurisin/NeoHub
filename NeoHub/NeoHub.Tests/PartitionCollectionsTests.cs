using System.Text.Json;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Logging;
using NeoHub.Services.Models;
using NeoHub.Tests.Infrastructure;
using static NeoHub.Tests.Infrastructure.ConditionsScenario;
using static NeoHub.Tests.PanelNotificationParserTests;

namespace NeoHub.Tests;

/// <summary>
/// Partitions and zones are added (by the first connect pull, or by a notification for a partition NeoHub did not
/// know) while notification handlers and full_state enumerate them on other threads: no failure, and every arm or
/// disarm is still announced.
/// </summary>
public class PartitionCollectionsTests
{
    private static TroubleDetailNotification Detail(string payload) => new() { Payload = Hex(payload) };

    [Fact]
    public async Task Handlers_that_enumerate_the_partitions_never_fail_while_partitions_are_added()
    {
        await using var host = await TestHost.StartAsync(0);
        var mediator = host.SessionMediator;
        int armsSent = 0, partition1Updates = 0;
        host.State.PartitionStateChanged += (_, e) =>
        {
            if (e.Partition.PartitionNumber == 1 && e.SessionId.StartsWith("X"))
                Interlocked.Increment(ref partition1Updates);
        };

        for (int i = 0; i < 600; i++)
        {
            var sessionId = $"X{i}";
            host.State.UpdatePartition(sessionId, new PartitionState { PartitionNumber = 1, Status = PartitionStatus.Disarmed });
            using var go = new ManualResetEventSlim();
            var writer = Task.Run(() =>
            {
                go.Wait();
                for (int p = 2; p <= 120; p++)
                    host.State.UpdatePartition(sessionId, new PartitionState { PartitionNumber = (byte)p });
            });
            var notifier = Task.Run(async () =>
            {
                go.Wait();
                for (int k = 0; k < 6; k++)
                {
                    Interlocked.Increment(ref armsSent);
                    await mediator.PublishNotificationAsync(sessionId,
                        new NotificationArmDisarm { Partition = 1, ArmMode = k % 2 == 0 ? ArmingMode.StayArm : ArmingMode.Disarm, UserId = 1 });
                    await mediator.PublishNotificationAsync(sessionId, Detail(k % 2 == 0 ? TamperOn : TamperOff));
                }
            });
            go.Set();
            await Task.WhenAll(writer, notifier);
        }

        Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Warning);
        // Each arm or disarm announces partition 1 (the trouble details may announce it more often).
        Assert.True(partition1Updates >= armsSent, $"{partition1Updates} updates for {armsSent} arm/disarm notifications");
    }

    [Fact]
    public async Task Full_state_never_fails_while_partitions_and_zones_are_added()
    {
        await using var host = await TestHost.StartAsync(0);
        var session = host.NewSession(new FakePanel(), "A");
        var loop = host.Connect(session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, session)));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));

        // The connected session's state is rebuilt over and over, so its dictionaries keep growing from empty.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                host.State.RemoveSession(Id);
                for (int n = 1; n <= 64; n++)
                {
                    host.State.UpdatePartition(Id, new PartitionState { PartitionNumber = (byte)n });
                    host.State.UpdateZone(Id, new ZoneState { ZoneNumber = (byte)n });
                }
            }
        });

        var mark = host.Client.Count;
        var requests = 0;
        while (!stop.IsCancellationRequested)
        {
            await host.FromClient("""{"type":"get_full_state"}""");
            requests++;
        }
        await writer;

        var types = host.Client.FramesSince(mark).Select(f => JsonDocument.Parse(f).RootElement.GetProperty("type").GetString()).ToList();
        Assert.True(requests > 10, $"{requests} requests");
        Assert.Equal(requests, types.Count(t => t == "full_state"));
        Assert.DoesNotContain("error", types);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Warning);

        session.EndConnection();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
