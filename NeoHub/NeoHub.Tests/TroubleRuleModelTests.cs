using System.Text.Json;
using DSC.TLink.ITv2.Messages;
using NeoHub.Services;
using NeoHub.Tests.Infrastructure;
using Xunit.Abstractions;
using static NeoHub.Tests.Infrastructure.ConditionsScenario;

namespace NeoHub.Tests;

/// <summary>
/// The trouble rules against an independent model (adapted from a review of addendum 4): random sequences of status
/// read requests and answers, trouble details, panel bit changes and connect pulls, compared step by step (state,
/// extra read requests and the exact partition_update frames). One partition behaves exactly as the decided rule
/// says; with two partitions NeoHub follows its documented refinement. Plus the cases where the two differ, with
/// their bounds.
/// </summary>
public class TroubleRuleModelTests
{
    private readonly ITestOutputHelper _out;
    public TroubleRuleModelTests(ITestOutputHelper output) => _out = output;

    internal static readonly (string Hex, (int, int, int) Tuple, string Label)[] Tuples =
    {
        ("01-00-01-09-01-00", (0x00, 0x09, 0x00), "ac_failure"),
        ("01-12-01-81-01-01", (0x12, 0x81, 0x01), "tamper"),
        ("01-00-01-87-01-00", (0x00, 0x87, 0x00), "battery_disconnected"),
        ("01-00-01-8A-01-00", (0x00, 0x8A, 0x00), "unknown_00_8a_00"),
    };

    /// <summary>
    /// The decided rule taken literally (Literal: a counted "none" of any partition clears every flag), or NeoHub's
    /// refinement (!Literal): a counted "none" clears every flag only when that partition showed troubles, troubles
    /// were forced by a trouble detail, or the set was not empty; otherwise only the set.
    /// </summary>
    internal sealed class Model
    {
        public readonly int N;
        public readonly bool IntervalOff;
        public readonly bool Literal;
        public long Seq, LastActive, LastSetChange;
        public readonly Dictionary<(int, int, int), string> Set = new();
        public readonly bool[] Flag;    // literal
        public readonly bool[] Showed;  // refinement: own read said troubles
        public bool Forced;             // refinement
        public bool Unknown;
        public bool Ready = true;
        public int Rereads;

        public Model(int n, bool intervalOff, bool literal)
        {
            N = n; IntervalOff = intervalOff; Literal = literal;
            Flag = new bool[n + 1]; Showed = new bool[n + 1];
        }

        private void All(bool[] a, bool v) { for (int i = 1; i <= N; i++) a[i] = v; }

        public long Request() => ++Seq;

        public void Answer(long req, int p, bool troubles)
        {
            if (troubles)
            {
                Flag[p] = true; Showed[p] = true;
                if (Set.Count == 0 && req > LastSetChange) Unknown = true;
                return;
            }
            if (LastActive > req) return;
            bool showed = Showed[p] || Forced || Set.Count > 0;
            if (Set.Count > 0) { Set.Clear(); LastSetChange = ++Seq; }
            if (Literal) { All(Flag, false); Unknown = false; return; }
            Forced = false;
            if (showed) { Unknown = false; All(Showed, false); }
        }

        public void Detail((int, int, int) t, string label, int state)
        {
            bool changed;
            if (state == 1)
            {
                changed = !Set.TryGetValue(t, out var l) || l != label;
                Set[t] = label;
                LastActive = ++Seq;
            }
            else changed = Set.Remove(t);
            if (changed) LastSetChange = ++Seq;

            if (!Ready) return;
            if (IntervalOff)
            {
                bool any = Set.Count > 0;
                All(Flag, any);
                Forced = any;
                if (!any) { Unknown = false; All(Showed, false); }
            }
            else
            {
                if (state == 1) { All(Flag, true); Forced = true; }
                if (Set.Count == 0) Rereads++;
            }
        }

        public void PullStarted() => Ready = false;

        public void PullDone()
        {
            if (Ready) return;
            Ready = true;
            if (Set.Count > 0) { All(Flag, true); Forced = true; }
        }

        public string StateOf(int p)
        {
            bool on = Literal ? Flag[p] : (Showed[p] || Forced);
            if (!on) return "off:";
            var labels = Set.Values.OrderBy(l => l, StringComparer.Ordinal).ToList();
            if (Unknown) labels.Add("unknown");
            return "on:" + string.Join(",", labels);
        }
    }

    private static List<string> PartitionUpdates(ConditionsScenario s, int mark)
    {
        return s.Host.Client.FramesSince(mark).Select(f => JsonDocument.Parse(f).RootElement)
            .Where(r => r.GetProperty("type").GetString() == "partition_update")
            .Select(r => $"p{r.GetProperty("partition_number").GetInt32()} "
                + (r.GetProperty("troubles").GetBoolean() ? "on:" : "off:")
                + string.Join(",", r.GetProperty("trouble_types").EnumerateArray().Select(t => t.GetString())))
            .ToList();
    }

    /// <summary>Panel truth for "agreeing" partitions: one bit for all; "disagree": p2 always reads false.</summary>
    public enum PanelKind { Single, Agree, Disagree }

    private async Task<(int Steps, List<string> Divergences)> RunOne(int seed, PanelKind kind, bool intervalOff, bool literal, int steps)
    {
        var rnd = new Random(seed);
        int n = kind == PanelKind.Single ? 1 : 2;
        await using var s = await StartAsync(interval: intervalOff ? 0 : 60, partitions: n);
        var m = new Model(n, intervalOff, literal);
        // Initial pull: no troubles.
        s.PullStarted(); m.PullStarted();
        for (int p = 1; p <= n; p++) { var r = s.Request(); var mr = m.Request(); s.Answer(r, (byte)p, false, StatusReadKind.ConnectPull); m.Answer(mr, p, false); }
        s.PullDone(); m.PullDone();

        var outstanding = new List<(long Impl, long Mod, int P, bool Pull)>();
        bool panelBit = false;
        var divergences = new List<string>();
        var log = new List<string>();

        for (int step = 0; step < steps; step++)
        {
            int mark = s.Host.Client.Count;
            var before = Enumerable.Range(1, n).Select(p => m.StateOf(p)).ToArray();
            string op;
            int roll = rnd.Next(100);
            bool pullOp = false;
            if (roll < 25 && outstanding.Count < 3)
            {
                int p = rnd.Next(1, n + 1);
                outstanding.Add((s.Request(), m.Request(), p, !m.Ready));
                op = $"req p{p}{(!m.Ready ? " (pull)" : "")}";
            }
            else if (roll < 55 && outstanding.Count > 0)
            {
                var i = rnd.Next(outstanding.Count);
                var o = outstanding[i];
                outstanding.RemoveAt(i);
                bool v = kind switch
                {
                    PanelKind.Single => rnd.Next(2) == 0,
                    PanelKind.Agree => panelBit,
                    _ => o.P == 1 ? rnd.Next(2) == 0 : false,
                };
                var k = o.Pull ? StatusReadKind.ConnectPull : StatusReadKind.Refresh;
                s.Answer(o.Impl, (byte)o.P, v, k);
                m.Answer(o.Mod, o.P, v);
                pullOp = o.Pull;
                op = $"ans p{o.P}={(v ? 1 : 0)}{(o.Pull ? " (pull)" : "")}";
            }
            else if (roll < 85)
            {
                var t = Tuples[rnd.Next(Tuples.Length)];
                int st = rnd.Next(10) switch { < 5 => 1, < 9 => 0, _ => 2 };
                await s.Detail($"{t.Hex}-{st:X2}");
                m.Detail(t.Tuple, t.Label, st);
                op = $"0x0823 {t.Label} {st:X2}";
            }
            else if (roll < 92)
            {
                panelBit = !panelBit;
                op = $"panel bit -> {panelBit}";
            }
            else if (m.Ready && roll < 96)
            {
                s.PullStarted(); m.PullStarted();
                op = "pull started";
            }
            else if (!m.Ready)
            {
                s.PullDone(); m.PullDone();
                op = "pull done";
            }
            else op = "noop";

            log.Add(op);
            var after = Enumerable.Range(1, n).Select(p => m.StateOf(p)).ToArray();
            var impl = Enumerable.Range(1, n).Select(p => s.StateOf((byte)p)).ToArray();
            for (int p = 1; p <= n; p++)
            {
                if (impl[p - 1] != after[p - 1])
                {
                    divergences.Add($"seed {seed} step {step} [{string.Join(" | ", log.TakeLast(8))}] p{p}: impl {impl[p - 1]} model {after[p - 1]}");
                    return (step, divergences);
                }
            }
            if (s.Rereads != m.Rereads)
            {
                divergences.Add($"seed {seed} step {step} [{string.Join(" | ", log.TakeLast(8))}] rereads impl {s.Rereads} model {m.Rereads}");
                return (step, divergences);
            }

            // Frames: exactly one per partition whose visible state changed (plus the pulled partition for a pull answer).
            var frames = PartitionUpdates(s, mark);
            var expected = new List<string>();
            for (int p = 1; p <= n; p++)
                if (before[p - 1] != after[p - 1]) expected.Add($"p{p} {after[p - 1]}");
            if (pullOp)
            {
                // The pull announces the partition it read whatever changed.
                var pulled = op.Substring(5, 1);
                if (!expected.Any(e => e.StartsWith("p" + pulled + " "))) expected.Add($"p{pulled} {after[int.Parse(pulled) - 1]}");
            }
            if (!frames.OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)))
            {
                divergences.Add($"seed {seed} step {step} [{string.Join(" | ", log.TakeLast(8))}] frames impl [{string.Join("; ", frames)}] expected [{string.Join("; ", expected)}]");
                return (step, divergences);
            }
            // Never "off" with types, never on-with-empty under the literal model comparisons is checked above.
        }
        return (steps, divergences);
    }

    private async Task<int> RunMany(PanelKind kind, bool intervalOff, bool literal)
    {
        int failures = 0;
        for (int seed = 1; seed <= 300 && failures <= 5; seed++)
        {
            var (_, d) = await RunOne(seed, kind, intervalOff, literal, steps: 120);
            foreach (var x in d) { _out.WriteLine(x); failures++; }
        }
        return failures;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_partition_follows_the_decided_rule(bool intervalOff) =>
        Assert.Equal(0, await RunMany(PanelKind.Single, intervalOff, literal: true));

    [Theory]
    [InlineData(PanelKind.Single, false)]
    [InlineData(PanelKind.Agree, false)]
    [InlineData(PanelKind.Disagree, false)]
    [InlineData(PanelKind.Single, true)]
    [InlineData(PanelKind.Agree, true)]
    [InlineData(PanelKind.Disagree, true)]
    public async Task Two_partitions_follow_the_refinement(PanelKind kind, bool intervalOff) =>
        Assert.Equal(0, await RunMany(kind, intervalOff, literal: false));

    [Fact]
    public async Task A_none_that_ends_troubles_from_the_connect_burst_clears_every_partition()
    {
        // The burst names two troubles; the pull reads p1 with troubles, then p2 without: the troubles have ended.
        await using var s = await StartAsync(interval: 60, partitions: 2);
        s.PullStarted();
        await s.Detail(TamperOn, burst: true);
        await s.Detail("01-00-01-8A-01-00-01", burst: true);
        var r1 = s.Request();
        var r2 = s.Request();
        s.Answer(r1, 1, true, StatusReadKind.ConnectPull);
        Assert.Equal("on:tamper,unknown_00_8a_00", s.StateOf(1));
        s.Answer(r2, 2, false, StatusReadKind.ConnectPull);
        s.PullDone();
        // As the decided rule says; before, p1 kept its flag with no trouble types for up to one interval.
        Assert.Equal(("off:", "off:"), (s.StateOf(1), s.StateOf(2)));

        // p1's next read still reports troubles: back on, as "unknown" (nothing explains them).
        s.Read(1, true);
        Assert.Equal(("on:unknown", "off:"), (s.StateOf(1), s.StateOf(2)));
    }

    [Fact]
    public async Task A_connect_burst_record_never_keeps_the_pull_from_counting()
    {
        // The connection loop applies a burst record after the pull's read was requested: it predates the
        // connection, so the pull's "none" counts and the flag stays off.
        await using var s = await StartAsync(interval: 60, partitions: 1);
        s.PullStarted();
        var r1 = s.Request();
        await s.Detail(TamperOn, burst: true);
        s.Answer(r1, 1, false, StatusReadKind.ConnectPull);
        s.PullDone();
        Assert.Equal("off:", s.StateOf(1));
    }

    [Fact]
    public async Task A_live_record_after_the_request_keeps_the_pull_from_counting()
    {
        // Positive control for the test above: the same order with a record that is not from the burst.
        await using var s = await StartAsync(interval: 60, partitions: 1);
        s.PullStarted();
        var r1 = s.Request();
        await s.Detail(TamperOn);
        s.Answer(r1, 1, false, StatusReadKind.ConnectPull);
        s.PullDone();
        Assert.Equal("on:tamper", s.StateOf(1));
    }

    [Fact]
    public async Task Bound_agreeing_partitions_read_at_different_moments()
    {
        // The refinement against the literal rule: p2 read the bit set, the bit cleared, and p1, which never
        // showed troubles, reads none. p2 keeps its flag until its own next read (in the same round, or one
        // interval after a re-read of p1 only); the literal rule would clear it at once.
        await using var s = await StartAsync(interval: 60, partitions: 2);
        s.Pull(false, false);
        s.Read(1, false);
        s.Read(2, true);
        Assert.Equal(("off:", "on:unknown"), (s.StateOf(1), s.StateOf(2)));
        s.Read(1, false);
        Assert.Equal(("off:", "on:unknown"), (s.StateOf(1), s.StateOf(2)));
        s.Read(2, false);
        Assert.Equal(("off:", "off:"), (s.StateOf(1), s.StateOf(2)));
    }

    [Fact]
    public async Task Bound_with_the_refresh_off_a_partition_that_read_troubles_keeps_them_until_a_trouble_detail()
    {
        // Refresh off: only the connect pull reads. p1 reads troubles and p2 none (in either order): p1 shows
        // "unknown" until a trouble detail leaves the set empty (the flags then follow the set) or a reconnect.
        await using var s = await StartAsync(interval: 0, partitions: 2);
        s.PullStarted();
        var r1 = s.Request();
        var r2 = s.Request();
        s.Answer(r1, 1, true, StatusReadKind.ConnectPull);
        s.Answer(r2, 2, false, StatusReadKind.ConnectPull);
        s.PullDone();
        Assert.Equal(("on:unknown", "off:"), (s.StateOf(1), s.StateOf(2)));
        await s.Detail(AcOff);
        Assert.Equal(("off:", "off:"), (s.StateOf(1), s.StateOf(2)));
    }
}
