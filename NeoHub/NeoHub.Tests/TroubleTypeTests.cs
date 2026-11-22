using NeoHub.Services;
using NeoHub.Tests.Infrastructure;
using static NeoHub.Tests.Infrastructure.ConditionsScenario;

namespace NeoHub.Tests;

/// <summary>
/// Trouble flags and trouble types from status reads and trouble details (0x0823), step by step, with the payloads
/// captured from a PowerSeries Neo panel (bytes only). Each step checks the partition_update frames it sent (exactly one
/// per partition whose troubles or trouble_types changed) and the resulting state.
/// </summary>
public class TroubleTypeTests
{
    [Fact]
    public async Task Captured_sequence_with_interleaved_reads()
    {
        await using var s = await StartAsync(interval: 60);
        s.Pull(false, false);
        s.Updates();
        var frames = new List<string>();
        List<string> Step() { var u = s.Updates(); frames.AddRange(u); return u; }

        // Before the captured window: the tamper tuple active (its bytes as captured later), then a periodic round.
        await s.Detail(TamperOn);
        Assert.Equal(new[] { "p1 on:tamper", "p2 on:tamper" }, Step());
        s.Read(1, true);
        s.Read(2, true);
        Assert.Empty(Step());

        // A read requested before the restore and answered after it: no "unknown" from it.
        var early = s.Request();
        await s.Detail(TamperOff);   // t = 0 s
        Assert.Equal(new[] { "p1 on:", "p2 on:" }, Step());   // the flag stays on until a read says otherwise
        Assert.Equal(1, s.Rereads);
        s.Answer(early, 1, true);
        Assert.Empty(Step());

        await s.Detail(AcOff);       // t = 1 s, restore of a tuple never seen: the set does not change
        Assert.Empty(Step());
        Assert.Equal(2, s.Rereads);  // the set is still empty: the extra read is asked for again

        // The extra read: no troubles.
        s.Read(1, false);
        Assert.Equal(new[] { "p1 off:" }, Step());
        s.Read(2, false);
        Assert.Equal(new[] { "p2 off:" }, Step());

        await s.Detail(AcOn);        // t = 72 s
        Assert.Equal(new[] { "p1 on:ac_failure", "p2 on:ac_failure" }, Step());
        s.Read(1, true);             // periodic round
        s.Read(2, true);
        Assert.Empty(Step());

        await s.Detail(TamperOn);    // t = 199 s
        Assert.Equal(new[] { "p1 on:ac_failure,tamper", "p2 on:ac_failure,tamper" }, Step());
        await s.Detail(TamperOff);   // t = 217 s
        Assert.Equal(new[] { "p1 on:ac_failure", "p2 on:ac_failure" }, Step());
        Assert.Equal(2, s.Rereads);  // the set is not empty
        s.Read(1, true);
        s.Read(2, true);
        Assert.Empty(Step());

        await s.Detail(AcOff);       // t = 371 s
        Assert.Equal(new[] { "p1 on:", "p2 on:" }, Step());
        Assert.Equal(3, s.Rereads);
        s.Read(1, false);            // the extra read
        s.Read(2, false);
        Assert.Equal(new[] { "p1 off:", "p2 off:" }, Step());

        // A read requested before the next state 01 and answered "no troubles" after it is not used for troubles.
        var stale = s.Request();
        await s.Detail(TamperOn);    // t = 396 s
        Assert.Equal(new[] { "p1 on:tamper", "p2 on:tamper" }, Step());
        s.Answer(stale, 1, false);
        Assert.Empty(Step());
        Assert.Equal("on:tamper", s.StateOf(1));
        s.Read(2, true);
        Assert.Empty(Step());

        await s.Detail(BatteryOn);   // t = 469 s
        Assert.Equal(new[] { "p1 on:battery_disconnected,tamper", "p2 on:battery_disconnected,tamper" }, Step());
        await s.Detail(BatteryOff);  // t = 514 s
        Assert.Equal(new[] { "p1 on:tamper", "p2 on:tamper" }, Step());
        await s.Detail(TamperOff);   // t = 520 s
        Assert.Equal(new[] { "p1 on:", "p2 on:" }, Step());
        Assert.Equal(4, s.Rereads);
        s.Read(1, false);            // t = 522 s
        s.Read(2, false);
        Assert.Equal(new[] { "p1 off:", "p2 off:" }, Step());

        Assert.DoesNotContain(frames, f => f.Contains(PartitionConditionsService.UnknownTroubleType));
        Assert.Equal(("off:", "off:"), (s.StateOf(1), s.StateOf(2)));
    }

    [Fact]
    public async Task Unknown_comes_from_a_read_after_the_last_set_change_and_stays_until_a_read_says_no_troubles()
    {
        await using var s = await StartAsync();
        s.Pull(true, false);
        Assert.Equal("on:unknown", s.StateOf(1));
        Assert.Equal("off:", s.StateOf(2));
        s.Updates();

        await s.Detail(TamperOn);
        Assert.Equal(new[] { "p1 on:tamper,unknown", "p2 on:tamper" }, s.Updates());
        await s.Detail(TamperOff);
        Assert.Equal(new[] { "p1 on:unknown", "p2 on:" }, s.Updates());

        // A read requested before the last set change does not add "unknown"; one requested after it does.
        await s.Detail(AcOn);
        var before = s.Request();
        await s.Detail(AcOff);
        s.Updates();
        s.Answer(before, 2, true);
        Assert.Equal("on:", s.StateOf(2));
        s.Read(2, true);
        Assert.Equal("on:unknown", s.StateOf(2));
        Assert.Equal(new[] { "p2 on:unknown" }, s.Updates());

        // Partition 1 says no troubles: its flag and "unknown" go, and so does the set; partition 2 keeps what its own
        // read gave until that read says otherwise.
        await s.Detail(BatteryOn);
        s.Updates();
        s.Read(1, false);
        Assert.Equal(new[] { "p1 off:", "p2 on:unknown" }, s.Updates());
        s.Read(2, false);
        Assert.Equal(new[] { "p2 off:" }, s.Updates());
    }

    [Fact]
    public async Task Partitions_that_report_differently_do_not_flap()
    {
        // No trouble detail at all; partition 1 reports troubles, partition 2 none, round after round.
        await using var s = await StartAsync();
        s.Pull(false, false);
        s.Updates();

        s.Read(1, true);
        s.Read(2, false);
        Assert.Equal(new[] { "p1 on:unknown" }, s.Updates());
        for (int round = 0; round < 3; round++)
        {
            s.Read(1, true);
            s.Read(2, false);
            Assert.Empty(s.Updates());
        }

        // A trouble detail turns partition 2 on as well; its own read that says no troubles clears what the detail
        // turned on, but not partition 1, whose own read reports troubles.
        await s.Detail(AcOn);
        Assert.Equal(new[] { "p1 on:ac_failure,unknown", "p2 on:ac_failure" }, s.Updates());
        s.Read(1, true);
        s.Read(2, false);
        Assert.Equal(new[] { "p1 on:unknown", "p2 off:" }, s.Updates());
        s.Read(1, true);
        s.Read(2, false);
        Assert.Empty(s.Updates());
    }

    [Fact]
    public async Task With_the_refresh_off_the_flags_follow_the_set()
    {
        await using var s = await StartAsync(interval: 0);
        s.Pull(true, true);
        Assert.Equal(("on:unknown", "on:unknown"), (s.StateOf(1), s.StateOf(2)));
        s.Updates();

        await s.Detail(AcOn);
        Assert.Equal(new[] { "p1 on:ac_failure,unknown", "p2 on:ac_failure,unknown" }, s.Updates());
        await s.Detail(TamperOn);
        Assert.Equal(new[] { "p1 on:ac_failure,tamper,unknown", "p2 on:ac_failure,tamper,unknown" }, s.Updates());
        await s.Detail(AcOff);
        Assert.Equal(new[] { "p1 on:tamper,unknown", "p2 on:tamper,unknown" }, s.Updates());
        await s.Detail(TamperOff);
        Assert.Equal(new[] { "p1 off:", "p2 off:" }, s.Updates());

        await s.Detail(BatteryOn);
        Assert.Equal(new[] { "p1 on:battery_disconnected", "p2 on:battery_disconnected" }, s.Updates());
        await s.Detail(AcOff);   // the set is not empty
        Assert.Empty(s.Updates());
        await s.Detail(BatteryOff);
        Assert.Equal(new[] { "p1 off:", "p2 off:" }, s.Updates());

        Assert.Equal(0, s.Rereads);   // no extra read with the refresh off
    }

    [Fact]
    public async Task Before_the_connect_pull_has_read_the_status_only_the_set_changes()
    {
        await using var s = await StartAsync();
        s.PullStarted();

        // Connect burst and early notifications: the set follows, the flags do not.
        await s.Detail(TamperOn, burst: true);
        await s.Detail(BatteryOn);
        await s.Detail(BatteryOff, burst: true);
        Assert.Empty(s.Updates());
        Assert.Equal(("off:", "off:"), (s.StateOf(1), s.StateOf(2)));
        Assert.Equal(0, s.Rereads);

        // The pull reads troubles: the set gives the types.
        s.Answer(s.Request(), 1, true, StatusReadKind.ConnectPull);
        Assert.Equal("on:tamper", s.StateOf(1));
        // A state 01 after partition 2's read was requested: its "no troubles" is not used, and the end of the status
        // step turns the flags on for the set that is still not empty.
        var p2 = s.Request();
        await s.Detail(AcOn);
        Assert.Equal("off:", s.StateOf(2));
        s.Answer(p2, 2, false, StatusReadKind.ConnectPull);
        Assert.Equal("off:", s.StateOf(2));
        s.Updates();
        s.PullDone();
        Assert.Equal(new[] { "p2 on:ac_failure,tamper" }, s.Updates());
        Assert.Equal("on:ac_failure,tamper", s.StateOf(1));

        // A connect-burst message after the pull still does not turn flags on (a takeover's burst, for example).
        s.Read(1, false);
        s.Read(2, false);
        s.Updates();
        await s.Detail(TamperOn, burst: true);
        Assert.Empty(s.Updates());
        await s.Detail(TamperOff, burst: true);
        Assert.Equal(0, s.Rereads);
    }

    [Fact]
    public async Task A_pull_that_reads_no_troubles_clears_the_set_of_the_connect_burst()
    {
        await using var s = await StartAsync();
        s.PullStarted();
        await s.Detail(TamperOn, burst: true);
        s.Answer(s.Request(), 1, false, StatusReadKind.ConnectPull);
        s.Answer(s.Request(), 2, false, StatusReadKind.ConnectPull);
        s.PullDone();
        Assert.Equal(("off:", "off:"), (s.StateOf(1), s.StateOf(2)));

        // The set is empty again: a later read with troubles gives "unknown", not "tamper".
        s.Read(1, true);
        Assert.Equal("on:unknown", s.StateOf(1));
    }

    [Theory]
    [InlineData("01-00-01-8A-01-00-01", "unknown_00_8a_00")]
    [InlineData("01-00-02-00-8A-01-00-01", "unknown_00_008a_00")]   // the value bytes as received
    [InlineData("01-12-01-81-01-02-01", "unknown_12_81_02")]
    public async Task Other_tuples_are_named_by_their_bytes_and_sorted_before_unknown(string payload, string label)
    {
        await using var s = await StartAsync();
        s.Pull(true, false);
        s.Updates();

        await s.Detail(payload);
        await s.Detail(TamperOn);
        var expected = new[] { label, "tamper" }.OrderBy(l => l, StringComparer.Ordinal).ToList();
        Assert.Equal(string.Join(",", expected.Append("unknown")), s.StateOf(1)[3..]);
        Assert.Equal(string.Join(",", expected), s.StateOf(2)[3..]);
    }

    [Fact]
    public async Task A_malformed_trouble_detail_is_logged_with_its_hex_and_ignored()
    {
        await using var s = await StartAsync();
        s.Pull(false, false);
        s.Updates();

        // The last one is two records with a 3-byte tail: nothing of it is applied, not even the first record.
        foreach (var payload in new[] { "01-00-01-09-01-00-01-00", "01-00-01-09-01-00-03", "01-00-00-01-00-01", "01-00-01",
                     "05-00-00-00-00-00-01-09-01-00-01", AcOn + "-" + TamperOn + "-01-12-01" })
            await s.Detail(payload);

        Assert.Empty(s.Updates());
        Assert.Equal(("off:", "off:"), (s.StateOf(1), s.StateOf(2)));
        Assert.Empty(s.Session.Conditions.ActiveTroubles);
        var ignored = s.Host.Logs.Entries
            .Where(e => e.Template.StartsWith("Ignoring a ModuleStatus_Trouble_Detail_Notification"))
            .Select(e => (e.Level, e.Text)).ToList();
        Assert.Equal(6, ignored.Count);
        Assert.All(ignored, e => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, e.Level));
        Assert.Contains(ignored, e => e.Text.EndsWith("[01-00-01-09-01-00-01-00]"));
    }

    [Fact]
    public async Task The_values_identify_a_tuple_whatever_their_byte_length()
    {
        await using var s = await StartAsync();
        s.Pull(false, false);
        s.Updates();

        // B = 0x09 sent as two bytes: still the AC failure, and restored by the one-byte form.
        await s.Detail("01-00-02-00-09-01-00-01");
        Assert.Equal(new[] { "p1 on:ac_failure", "p2 on:ac_failure" }, s.Updates());
        await s.Detail(AcOff);
        Assert.Empty(s.Session.Conditions.ActiveTroubles);
        Assert.Equal(1, s.Rereads);
    }

    [Fact]
    public async Task State_02_in_memory_counts_as_restored()
    {
        await using var s = await StartAsync();
        s.Pull(false, false);
        s.Updates();

        await s.Detail(TamperOn);
        await s.Detail(AcOn);
        s.Updates();
        await s.Detail("01-12-01-81-01-01-02");   // tamper in memory
        Assert.Equal(new[] { "p1 on:ac_failure", "p2 on:ac_failure" }, s.Updates());
        Assert.Equal(0, s.Rereads);
        await s.Detail("01-00-01-09-01-00-02");   // AC failure in memory: the set is empty, the flags stay on
        Assert.Equal(new[] { "p1 on:", "p2 on:" }, s.Updates());
        Assert.Equal(1, s.Rereads);

        Assert.Equal(new[] { "Trouble tamper in memory (treated as restored)", "Trouble ac_failure in memory (treated as restored)" },
            s.Host.Logs.Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information && e.Text.Contains("in memory"))
                .Select(e => e.Text));
    }

    [Fact]
    public async Task The_records_of_one_message_are_applied_in_order_and_announced_once()
    {
        await using var s = await StartAsync();
        s.Pull(false, false);
        s.Updates();

        // Two records: AC failure and tamper active, one frame per partition.
        await s.Detail(AcOn + "-" + TamperOn);
        Assert.Equal(new[] { "p1 on:ac_failure,tamper", "p2 on:ac_failure,tamper" }, s.Updates());

        // Active then restored in the same message: the tuple is gone, the flag stays on, the extra read is asked for.
        await s.Detail(BatteryOn + "-" + BatteryOff);
        Assert.Empty(s.Updates());
        await s.Detail(AcOff + "-" + TamperOff);
        Assert.Equal(new[] { "p1 on:", "p2 on:" }, s.Updates());
        Assert.Equal(1, s.Rereads);

        // A state 01 after a read was requested, as the second record: that read's "no troubles" is not used.
        var stale = s.Request();
        await s.Detail(AcOff + "-" + AcOn);
        s.Updates();
        s.Answer(stale, 1, false);
        Assert.Equal("on:ac_failure", s.StateOf(1));
    }

    [Fact]
    public async Task Trouble_details_are_logged_at_Information_with_their_label()
    {
        await using var s = await StartAsync();
        s.PullStarted();
        await s.Detail(TamperOn, burst: true);
        s.PullDone();
        await s.Detail(AcOn);
        await s.Detail(AcOff);

        var infos = s.Host.Logs.Entries
            .Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information && e.Text.StartsWith("Trouble "))
            .Select(e => e.Text).ToList();
        Assert.Equal(new[]
        {
            "Trouble tamper active (tuple 12_81_01) before the session was ready, in the connect burst; the connect pull decides the trouble flag",
            "Trouble ac_failure active",
            "Trouble ac_failure restored",
        }, infos);
        Assert.Contains(s.Host.Logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Debug
            && e.Text == "ModuleStatus_Trouble_Detail_Notification payload [01-00-01-09-01-00-01]");
    }
}
