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
/// The trouble flag, trouble types, siren and alarm memory of a partition change together; full_state and
/// partition_update, built on other threads, must always show the four values of one moment.
/// </summary>
public class ConditionValuesTests
{
    private static TroubleDetailNotification Detail(string payload) => new() { Payload = Hex(payload) };

    [Fact]
    public void The_four_values_are_replaced_together_and_the_setters_keep_the_others()
    {
        var p = new PartitionState { PartitionNumber = 1 };
        Assert.Same(PartitionConditionValues.None, p.ConditionValues);

        var values = new PartitionConditionValues(true, new[] { "tamper" }, true, false);
        p.ConditionValues = values;
        Assert.Same(values, p.ConditionValues);
        Assert.Equal((true, "tamper", true, false), (p.TroublesPresent, string.Join(",", p.TroubleTypes), p.SirenActive, p.AlarmsInMemory));

        p.AlarmsInMemory = true;
        Assert.Equal((true, "tamper", true, true), (p.TroublesPresent, string.Join(",", p.TroubleTypes), p.SirenActive, p.AlarmsInMemory));
        Assert.Throws<ArgumentNullException>(() => p.ConditionValues = null!);
    }

    [Fact]
    public async Task Frames_never_mix_two_moments_while_troubles_change_on_other_threads()
    {
        // Trouble refresh off: the flag follows the trouble details, so a partition is either "off" with [] or
        // "on" with [tamper]. Anything else in a frame would mix two moments.
        await using var host = await TestHost.StartAsync(0);
        var session = host.NewSession(new FakePanel(), "A");
        var loop = host.Connect(session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, session)));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
        var mediator = host.SessionMediator;
        var mark = host.Client.Count;

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var troubles = Task.Run(async () =>
        {
            for (int k = 0; !stop.IsCancellationRequested; k++)
                await mediator.PublishNotificationAsync(Id, Detail(k % 2 == 0 ? TamperOn : TamperOff));
        });
        // A second writer: arm and disarm notifications announce partitions outside the trouble state's lock.
        var arming = Task.Run(async () =>
        {
            for (int k = 0; !stop.IsCancellationRequested; k++)
                await mediator.PublishNotificationAsync(Id, new NotificationArmDisarm
                {
                    Partition = (k % 2) + 1,
                    ArmMode = k % 4 < 2 ? ArmingMode.StayArm : ArmingMode.Disarm,
                    UserId = 1
                });
        });
        var fullStates = 0;
        while (!stop.IsCancellationRequested)
        {
            await host.FromClient("""{"type":"get_full_state"}""");
            fullStates++;
        }
        await Task.WhenAll(troubles, arming);

        var seen = new Dictionary<string, int>();
        var mixed = new List<string>();
        foreach (var frame in host.Client.FramesSince(mark))
        {
            var root = JsonDocument.Parse(frame).RootElement;
            var type = root.GetProperty("type").GetString();
            IEnumerable<JsonElement> partitions = type switch
            {
                "full_state" => root.GetProperty("sessions").EnumerateArray().SelectMany(s => s.GetProperty("partitions").EnumerateArray()).ToList(),
                "partition_update" => new[] { root },
                _ => Array.Empty<JsonElement>(),
            };
            foreach (var p in partitions)
            {
                var shown = (p.GetProperty("troubles").GetBoolean() ? "on:" : "off:")
                    + string.Join(",", p.GetProperty("trouble_types").EnumerateArray().Select(t => t.GetString()));
                seen[shown] = seen.GetValueOrDefault(shown) + 1;
                if (shown is not ("off:" or "on:tamper"))
                    mixed.Add($"{type}: {shown}");
            }
        }

        Assert.True(fullStates > 10, $"{fullStates} full_state requests");
        Assert.True(seen.GetValueOrDefault("on:tamper") > 0 && seen.GetValueOrDefault("off:") > 0, string.Join(", ", seen));
        Assert.True(mixed.Count == 0, $"{mixed.Count} mixed: {string.Join("; ", mixed.Take(5))}");
        Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Error);

        session.EndConnection();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
