using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Options;
using NeoHub.Services.Models;
using NeoHub.Services.Settings;

namespace NeoHub.Services
{
    /// <summary>Which status read is applied: the connect pull, or a re-read of <see cref="PartitionTroubleRefreshService"/>.</summary>
    public enum StatusReadKind
    {
        ConnectPull,
        Refresh,
    }

    public sealed class TroubleRereadRequestedEventArgs : EventArgs
    {
        public required string SessionId { get; init; }
    }

    /// <summary>
    /// Derives the partitions' trouble flag (<see cref="PartitionState.TroublesPresent"/>) and trouble types
    /// (<see cref="PartitionState.TroubleTypes"/>) from the status reads (<see cref="ModulePartitionStatus"/>: the connect
    /// pull and the re-reads of <see cref="PartitionTroubleRefreshService"/>) and from the panel's trouble details
    /// (<see cref="TroubleDetailNotification"/>, 0x0823). The state lives on <see cref="SessionState.Conditions"/>, so
    /// any instance of this service works on the same data.
    ///
    /// Rules:
    /// <list type="bullet">
    /// <item>One panel-wide set of active troubles per session, keyed by the 0x0823 tuple. A state 01 adds its tuple, a
    /// state 00 removes it.</item>
    /// <item>After the connect pull has read the partition status, a state 01 turns the flag on for every partition at
    /// once. Before that (and for a message from the connect burst) only the set changes; when the pull has read the
    /// status, a set that is still not empty turns the flags on.</item>
    /// <item>A state 00 never turns a flag off. When it leaves the set empty, one extra re-read is requested
    /// (<see cref="TroubleRereadRequested"/>).</item>
    /// <item>Each partition keeps the flag its own reads give, as before trouble details existed. A read that reports
    /// no troubles clears that partition's flag, the whole set and the flags turned on by a state 01, unless a state 01
    /// arrived after the read was requested (<see cref="BeginStatusRead"/>): then the read is not used for troubles.</item>
    /// <item>"unknown" is added to a partition's trouble types when its own read, requested after the last change of
    /// the set, reports troubles while the set is empty; it stays until that partition's read reports none.</item>
    /// <item>With the trouble refresh off (interval 0) the flags follow the set after each 0x0823: on while it is not
    /// empty, off (including what the connect pull read) when it is.</item>
    /// <item>Labels only for proven tuples; any other tuple is unknown_&lt;A&gt;_&lt;B&gt;_&lt;C&gt;. The list is the
    /// labels in ordinal order, then "unknown"; empty while the partition's flag is off.</item>
    /// </list>
    /// Partitions are only updated, never created, and a partition_update is sent only for a visible change.
    /// </summary>
    public sealed class PartitionConditionsService
    {
        /// <summary>Trouble type of troubles a status read reports but no trouble detail explains.</summary>
        public const string UnknownTroubleType = "unknown";

        /// <summary>Labels of the proven tuples (value bytes as received). These names are a stable API.</summary>
        private static readonly IReadOnlyDictionary<string, string> KnownTroubleTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["00_09_00"] = "ac_failure",
            ["12_81_01"] = "tamper",
            ["00_87_00"] = "battery_disconnected",
        };

        private readonly IPanelStateService _panelState;
        private readonly IOptionsMonitor<PanelConnectionsSettings> _settings;
        private readonly ILogger<PartitionConditionsService> _logger;

        public PartitionConditionsService(
            IPanelStateService panelState,
            IOptionsMonitor<PanelConnectionsSettings> settings,
            ILogger<PartitionConditionsService> logger)
        {
            _panelState = panelState;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>A state 00 left the set of active troubles empty: the session's partitions should be read again soon.</summary>
        public event EventHandler<TroubleRereadRequestedEventArgs>? TroubleRereadRequested;

        /// <summary>The trouble type of a tuple: its label when proven, otherwise unknown_&lt;A&gt;_&lt;B&gt;_&lt;C&gt;.</summary>
        public static string TroubleTypeLabel(TroubleDetail detail) =>
            KnownTroubleTypes.TryGetValue(detail.Key, out var label) ? label : "unknown_" + detail.Key;

        #region Status reads

        /// <summary>To be called right before a ModulePartitionStatus request is sent; its result goes to <see cref="ApplyStatusRead"/>.</summary>
        public long BeginStatusRead(SessionState session)
        {
            lock (session.Conditions.Gate)
                return ++session.Conditions.Sequence;
        }

        /// <summary>
        /// Applies a status read of <paramref name="partition"/> (which the connect pull may not have stored yet): its
        /// trouble flag, under the rules above. Announces every other partition whose visible state changed, and this one
        /// too unless it was read by the connect pull (<paramref name="kind"/>), whose caller announces it. Returns
        /// whether this partition's visible state changed.
        /// </summary>
        public bool ApplyStatusRead(
            string sessionId,
            SessionState session,
            PartitionState partition,
            ModulePartitionStatus.PartitionStatus2 status2,
            long requestedAt,
            StatusReadKind kind,
            ILogger log)
        {
            var c = session.Conditions;
            var troubles = status2.HasFlag(ModulePartitionStatus.PartitionStatus2.TroublesPresent);
            lock (c.Gate)
            {
                var partitions = Snapshot(session, partition);
                var before = Capture(c, partitions);
                var own = c.Partitions[partition.PartitionNumber];

                if (troubles)
                {
                    own.ReadTroubles = true;
                    if (c.ActiveTroubles.Count == 0 && requestedAt > c.LastTroubleSetChangeAt)
                        own.Unknown = true;
                }
                else if (c.LastTroubleActiveAt > requestedAt)
                {
                    log.LogDebug(
                        "Status read of partition {Partition} not used for troubles: a trouble became active after it was requested",
                        partition.PartitionNumber);
                }
                else
                {
                    ClearTroubleSet(c);
                    c.TroublesForced = false;
                    own.ReadTroubles = false;
                    own.Unknown = false;
                }

                return Publish(sessionId, session, partitions, before, kind == StatusReadKind.ConnectPull ? partition : null, log)
                    .Contains(partition);
            }
        }

        /// <summary>The connect pull of a (new or taking over) connection starts: trouble details no longer turn flags on.</summary>
        public void ConnectPullStarted(SessionState session)
        {
            lock (session.Conditions.Gate)
                session.Conditions.ConnectPullDone = false;
        }

        /// <summary>
        /// The connect pull has read the partition status (or ended without it). From now on a trouble detail turns the
        /// flags on; a set of active troubles that is still not empty does so now. Repeated calls do nothing.
        /// </summary>
        public void ConnectPullStatusDone(string sessionId, SessionState session, ILogger log)
        {
            var c = session.Conditions;
            lock (c.Gate)
            {
                if (c.ConnectPullDone)
                    return;
                var partitions = Snapshot(session, null);
                var before = Capture(c, partitions);
                c.ConnectPullDone = true;
                if (c.ActiveTroubles.Count > 0)
                    c.TroublesForced = true;
                Publish(sessionId, session, partitions, before, null, log);
            }
        }

        #endregion

        #region Trouble details (0x0823)

        /// <summary>Applies a parsed 0x0823 of a session, under the rules above, and logs it at Information.</summary>
        public void ApplyTroubleDetail(string sessionId, TroubleDetail detail, bool inConnectBurst, ILogger log)
        {
            var session = GetOrCreateSession(sessionId);
            var c = session.Conditions;
            var label = TroubleTypeLabel(detail);
            var state = detail.Active ? "active" : "restored";
            bool reread = false;

            lock (c.Gate)
            {
                var ready = c.ConnectPullDone && !inConnectBurst;
                if (ready)
                    log.LogInformation("Trouble {TroubleType} {TroubleState}", label, state);
                else
                    log.LogInformation(
                        "Trouble {TroubleType} {TroubleState} (tuple {Tuple}) before the session was ready{Burst}; the connect pull decides the trouble flag",
                        label, state, detail.Key, inConnectBurst ? ", in the connect burst" : string.Empty);

                var partitions = Snapshot(session, null);
                var before = Capture(c, partitions);

                bool setChanged;
                if (detail.Active)
                {
                    setChanged = !c.ActiveTroubles.ContainsKey(detail.Key);
                    c.ActiveTroubles[detail.Key] = label;
                    c.LastTroubleActiveAt = ++c.Sequence;
                }
                else
                {
                    setChanged = c.ActiveTroubles.Remove(detail.Key);
                }
                if (setChanged)
                    c.LastTroubleSetChangeAt = ++c.Sequence;

                if (ready)
                {
                    if (IsRefreshOff(sessionId))
                    {
                        // No re-reads: the flags follow the set.
                        c.TroublesForced = c.ActiveTroubles.Count > 0;
                        if (c.ActiveTroubles.Count == 0)
                        {
                            foreach (var p in c.Partitions.Values)
                            {
                                p.ReadTroubles = false;
                                p.Unknown = false;
                            }
                        }
                    }
                    else if (detail.Active)
                    {
                        c.TroublesForced = true;
                    }
                    else if (c.ActiveTroubles.Count == 0)
                    {
                        // Never cleared here: the re-read tells whether anything else is still active.
                        reread = true;
                    }
                }

                Publish(sessionId, session, partitions, before, null, log);
            }

            if (reread)
                TroubleRereadRequested?.Invoke(this, new TroubleRereadRequestedEventArgs { SessionId = sessionId });
        }

        #endregion

        #region Helpers

        private bool IsRefreshOff(string sessionId) =>
            (_settings.CurrentValue.FindBySessionId(sessionId)?.TroubleRefreshIntervalSeconds
             ?? ConnectionSettings.DefaultTroubleRefreshIntervalSeconds) <= 0;

        private SessionState GetOrCreateSession(string sessionId)
        {
            var session = _panelState.GetSession(sessionId);
            if (session is not null)
                return session;
            // A connect-burst message can come before the connect pull created the state.
            _logger.LogDebug("Creating the state of session {SessionId} for a panel notification", sessionId);
            _panelState.UpdateSession(sessionId, _ => { });
            return _panelState.GetSession(sessionId)!;
        }

        private static void ClearTroubleSet(PanelConditionsState c)
        {
            if (c.ActiveTroubles.Count == 0)
                return;
            c.ActiveTroubles.Clear();
            c.LastTroubleSetChangeAt = ++c.Sequence;
        }

        /// <summary>The session's partitions, plus one the connect pull has not stored yet. The pull may add partitions meanwhile.</summary>
        private static List<PartitionState> Snapshot(SessionState session, PartitionState? extra)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var list = session.Partitions.Values.ToList();
                    if (extra is not null && !list.Contains(extra))
                        list.Add(extra);
                    return list;
                }
                catch (InvalidOperationException) when (attempt < 20)
                {
                    Thread.Yield();
                }
            }
        }

        private readonly record struct Visible(bool Troubles, string TroubleTypes);

        private static Visible VisibleOf(PartitionState p) => new(p.TroublesPresent, string.Join(",", p.TroubleTypes));

        /// <summary>The visible state of each partition, creating the per-partition entries first.</summary>
        private static Dictionary<PartitionState, Visible> Capture(PanelConditionsState c, List<PartitionState> partitions)
        {
            var before = new Dictionary<PartitionState, Visible>(ReferenceEqualityComparer.Instance);
            foreach (var p in partitions)
            {
                if (!c.Partitions.ContainsKey(p.PartitionNumber))
                    c.Partitions[p.PartitionNumber] = new PartitionConditions { ReadTroubles = p.TroublesPresent };
                before[p] = VisibleOf(p);
            }
            return before;
        }

        /// <summary>
        /// Recomputes every partition from the tracked state and announces the ones whose visible state changed, except
        /// <paramref name="pulled"/> (announced by the connect pull, whose flag change is logged at Debug there).
        /// Returns the partitions that changed.
        /// </summary>
        private List<PartitionState> Publish(
            string sessionId,
            SessionState session,
            List<PartitionState> partitions,
            Dictionary<PartitionState, Visible> before,
            PartitionState? pulled,
            ILogger log)
        {
            var c = session.Conditions;
            var labels = c.ActiveTroubles.Values.OrderBy(l => l, StringComparer.Ordinal).ToArray();
            var changed = new List<PartitionState>();

            foreach (var p in partitions)
            {
                var entry = c.Partitions[p.PartitionNumber];
                var flag = entry.ReadTroubles || c.TroublesForced;
                var types = !flag ? Array.Empty<string>()
                    : entry.Unknown ? labels.Append(UnknownTroubleType).ToArray()
                    : labels;

                var old = before[p];
                p.TroublesPresent = flag;
                if (string.Join(",", types) != old.TroubleTypes)
                    p.TroubleTypes = types;
                if (VisibleOf(p) == old)
                    continue;

                changed.Add(p);
                if (ReferenceEquals(p, pulled))
                    continue;
                if (flag != old.Troubles)
                    log.LogInformation("Partition {Partition} troubles {Troubles}", p.PartitionNumber, flag ? "present" : "cleared");
                // Never recreates a partition or session that was removed meanwhile.
                _panelState.TryUpdatePartition(sessionId, p);
            }
            return changed;
        }

        #endregion
    }
}
