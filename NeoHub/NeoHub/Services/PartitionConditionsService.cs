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

    public sealed class KeypadAlarmEventArgs : EventArgs
    {
        public required string SessionId { get; init; }
        public required KeypadAlarmRecord Alarm { get; init; }
    }

    /// <summary>
    /// Derives the partitions' trouble flag (<see cref="PartitionState.TroublesPresent"/>), trouble types
    /// (<see cref="PartitionState.TroubleTypes"/>), siren (<see cref="PartitionState.SirenActive"/>) and alarm memory
    /// (<see cref="PartitionState.AlarmsInMemory"/>) from the status reads (<see cref="ModulePartitionStatus"/>: the
    /// connect pull and the re-reads of <see cref="PartitionTroubleRefreshService"/>; of PartitionStatus2 only
    /// TroublesPresent, AudibleBellSiren and AlarmsInMemory are used here) and from the panel's notifications: trouble
    /// details (<see cref="TroubleDetailNotification"/>, 0x0823), audible bell (<see cref="PartitionAudibleBellNotification"/>,
    /// 0x023B) and alarm memory (<see cref="PartitionAlarmMemoryNotification"/>, 0x023C). It also passes keypad alarms
    /// (<see cref="NonZoneAlarmStatusNotification"/>, 0x0841) on. The state lives on <see cref="SessionState.Conditions"/>,
    /// so any instance of this service works on the same data.
    ///
    /// Trouble rules:
    /// <list type="bullet">
    /// <item>One panel-wide set of active troubles per session, keyed by the values of the 0x0823 tuple. A state 01 adds
    /// its tuple, a state 00 (restored) or 02 (in memory) removes it. The records of one message are applied in order
    /// and announced once.</item>
    /// <item>After the connect pull has read the partition status, a state 01 turns the flag on for every partition at
    /// once. Before that (and for a message from the connect burst) only the set changes; when the pull has read the
    /// status, a set that is still not empty turns the flags on.</item>
    /// <item>A state 00 or 02 never turns a flag off. When the set is empty after it, one extra re-read is requested
    /// (<see cref="TroubleRereadRequested"/>), also when it was empty already.</item>
    /// <item>Each partition keeps the flag its own reads give, as before trouble details existed. A read that reports
    /// no troubles clears that partition's flag, the whole set and the flags turned on by a state 01, unless a state 01
    /// arrived after the read was requested (<see cref="BeginStatusRead"/>): then the read is not used for troubles.</item>
    /// <item>"unknown" is added to a partition's trouble types when its own read, requested after the last change of
    /// the set, reports troubles while the set is empty; it stays until that partition's read reports none.</item>
    /// <item>With the trouble refresh off (interval 0) the flags follow the set after each 0x0823: on while it is not
    /// empty, off (including what the connect pull read) when it is.</item>
    /// <item>Labels only for the exact values of proven tuples; any other tuple is unknown_&lt;A&gt;_&lt;B&gt;_&lt;C&gt;
    /// from its value bytes as received. The list is the labels in ordinal order, then "unknown"; empty while the
    /// partition's flag is off.</item>
    /// </list>
    /// Siren and alarm memory rules (per partition), where a status read never undoes a notification that arrived after
    /// the read was requested:
    /// <list type="bullet">
    /// <item>An audible bell "on" turns the siren on; "off" turns it off, and so does an arm or disarm notification of
    /// the partition (<see cref="ClearSirenForArmDisarm"/>), so a missed "off" cannot hide the next alarm.</item>
    /// <item>The connect pull sets the siren from AudibleBellSiren (it was on and stays on only for a bell "on" that
    /// arrived after the pull's read was requested). A later re-read may turn it on from the bit but never turns off an
    /// "on" from the bell (a disagreement is logged at Information once); a siren that only the bit turned on goes off
    /// with a read without the bit.</item>
    /// <item>Alarm memory follows AlarmsInMemory of every read and 0x023C (nonzero sets, zero clears).</item>
    /// </list>
    /// Partitions are only updated, never created, and a partition_update is sent only for a visible change.
    /// </summary>
    public sealed class PartitionConditionsService
    {
        /// <summary>Trouble type of troubles a status read reports but no trouble detail explains.</summary>
        public const string UnknownTroubleType = "unknown";

        /// <summary>Keypad alarm types: alarm type 03 is the fire alarm; any other value is not identified.</summary>
        public const string FireKeypadAlarm = "fire";
        public const string UnknownKeypadAlarm = "unknown";

        /// <summary>Labels of the proven tuples (exact values of A, B and C). These names are a stable API.</summary>
        private static readonly IReadOnlyDictionary<(int, int, int), string> KnownTroubleTypes = new Dictionary<(int, int, int), string>
        {
            [(0x00, 0x09, 0x00)] = "ac_failure",
            [(0x12, 0x81, 0x01)] = "tamper",
            [(0x00, 0x87, 0x00)] = "battery_disconnected",
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

        /// <summary>The panel reported a keypad alarm (state 01), for any partition number.</summary>
        public event EventHandler<KeypadAlarmEventArgs>? KeypadAlarm;

        /// <summary>
        /// The trouble type of a tuple: its label when its values are those of a proven tuple, otherwise
        /// unknown_&lt;A&gt;_&lt;B&gt;_&lt;C&gt; from the value bytes as received (<see cref="TroubleDetail.RawKey"/>).
        /// </summary>
        public static string TroubleTypeLabel(TroubleDetail detail) =>
            KnownTroubleTypes.TryGetValue(detail.Tuple, out var label) ? label : "unknown_" + detail.RawKey;

        #region Status reads

        /// <summary>To be called right before a ModulePartitionStatus request is sent; its result goes to <see cref="ApplyStatusRead"/>.</summary>
        public long BeginStatusRead(SessionState session)
        {
            lock (session.Conditions.Gate)
                return ++session.Conditions.Sequence;
        }

        /// <summary>
        /// Applies a status read of <paramref name="partition"/> (which the connect pull may not have stored yet): its
        /// trouble flag, siren and alarm memory bits, under the rules above. Announces every other partition whose
        /// visible state changed, and this one too unless it was read by the connect pull (<paramref name="kind"/>),
        /// whose caller announces it. Returns whether this partition's visible state changed.
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

                ApplySirenBit(own, partition.PartitionNumber,
                    status2.HasFlag(ModulePartitionStatus.PartitionStatus2.AudibleBellSiren), requestedAt, kind, log);
                ApplyMemoryBit(own, partition.PartitionNumber,
                    status2.HasFlag(ModulePartitionStatus.PartitionStatus2.AlarmsInMemory), requestedAt, log);

                return Publish(sessionId, session, partitions, before, kind == StatusReadKind.ConnectPull ? partition : null, log)
                    .Contains(partition);
            }
        }

        private static void ApplySirenBit(PartitionConditions own, byte partition, bool bit, long requestedAt, StatusReadKind kind, ILogger log)
        {
            var clearedSince = own.LastSirenClearAt > requestedAt;
            if (kind == StatusReadKind.ConnectPull)
            {
                // The pull sets the siren from the bit; only a bell "on" newer than its request keeps the siren on.
                if (own.LastBellOnAt > requestedAt)
                {
                    if (!bit)
                        log.LogDebug("Partition {Partition} siren kept on: the panel reported it on after the status read was requested", partition);
                }
                else
                {
                    own.SirenFromBell = false;
                }
                own.SirenFromBit = bit && !clearedSince;
                return;
            }

            if (bit)
            {
                if (clearedSince)
                    log.LogDebug("Status read of partition {Partition} not used for the siren: it was turned off after the read was requested", partition);
                else
                    own.SirenFromBit = true;
                return;
            }

            own.SirenFromBit = false;
            if (own.SirenFromBell && !own.SirenDisagreementLogged)
            {
                own.SirenDisagreementLogged = true;
                log.LogInformation(
                    "Partition {Partition}: the status read shows no siren, but the panel reported the siren on and not off since; keeping it on",
                    partition);
            }
        }

        private static void ApplyMemoryBit(PartitionConditions own, byte partition, bool bit, long requestedAt, ILogger log)
        {
            if (bit && own.LastMemoryClearAt > requestedAt)
                log.LogDebug("Status read of partition {Partition} not used for the alarm memory: it was cleared after the read was requested", partition);
            else if (!bit && own.LastMemorySetAt > requestedAt)
                log.LogDebug("Status read of partition {Partition} not used for the alarm memory: it was set after the read was requested", partition);
            else
                own.AlarmMemory = bit;
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

        /// <summary>
        /// Applies the records of a parsed 0x0823 of a session in order, under the rules above, and logs each at
        /// Information. The partitions are announced once, after the last record.
        /// </summary>
        public void ApplyTroubleDetails(string sessionId, IReadOnlyList<TroubleDetail> details, bool inConnectBurst, ILogger log)
        {
            var session = GetOrCreateSession(sessionId);
            var c = session.Conditions;
            bool reread = false;

            lock (c.Gate)
            {
                var ready = c.ConnectPullDone && !inConnectBurst;
                var refreshOff = IsRefreshOff(sessionId);
                var partitions = Snapshot(session, null);
                var before = Capture(c, partitions);

                foreach (var detail in details)
                {
                    var label = TroubleTypeLabel(detail);
                    var state = detail.State switch
                    {
                        TroubleDetailState.Active => "active",
                        TroubleDetailState.InMemory => "in memory (treated as restored)",
                        _ => "restored",
                    };
                    if (ready)
                        log.LogInformation("Trouble {TroubleType} {TroubleState}", label, state);
                    else
                        log.LogInformation(
                            "Trouble {TroubleType} {TroubleState} (tuple {Tuple}) before the session was ready{Burst}; the connect pull decides the trouble flag",
                            label, state, detail.RawKey, inConnectBurst ? ", in the connect burst" : string.Empty);

                    bool setChanged;
                    if (detail.Active)
                    {
                        setChanged = !c.ActiveTroubles.TryGetValue(detail.Tuple, out var stored) || stored != label;
                        c.ActiveTroubles[detail.Tuple] = label;
                        c.LastTroubleActiveAt = ++c.Sequence;
                    }
                    else
                    {
                        // Restored, or in memory: no longer active.
                        setChanged = c.ActiveTroubles.Remove(detail.Tuple);
                    }
                    if (setChanged)
                        c.LastTroubleSetChangeAt = ++c.Sequence;

                    if (!ready)
                        continue;
                    if (refreshOff)
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
                }

                // Never cleared by a restore: the extra read tells whether anything else is still active.
                if (ready && !refreshOff && c.ActiveTroubles.Count == 0 && details.Any(d => !d.Active))
                    reread = true;

                Publish(sessionId, session, partitions, before, null, log);
            }

            if (reread)
                TroubleRereadRequested?.Invoke(this, new TroubleRereadRequestedEventArgs { SessionId = sessionId });
        }

        #endregion

        #region Siren (0x023B) and alarm memory (0x023C)

        /// <summary>
        /// Applies a parsed 0x023B: the partition's siren on or off. Logged at Information, with every field at Debug.
        /// A partition NeoHub does not know is not created.
        /// </summary>
        public void ApplyAudibleBell(string sessionId, AudibleBell bell, ILogger log)
        {
            log.LogInformation("Audible bell of partition {Partition} {BellState}", bell.Partition, bell.On ? "on" : "off");
            log.LogDebug("Audible bell: partition {Partition}, on/off byte 0x{OnOff:X2}, rest {Rest}",
                bell.Partition, bell.OnOffByte, new DSC.TLink.Extensions.HexBytes(bell.Rest));

            UpdateKnownPartition(sessionId, bell.Partition, "audible bell", log, (c, own) =>
            {
                if (bell.On)
                {
                    own.SirenFromBell = true;
                    own.SirenDisagreementLogged = false;
                    own.LastBellOnAt = ++c.Sequence;
                }
                else
                {
                    own.SirenFromBell = false;
                    own.SirenFromBit = false;
                    own.LastSirenClearAt = ++c.Sequence;
                }
            });
        }

        /// <summary>
        /// Applies a parsed 0x023C: nonzero sets the partition's alarm memory, zero clears it. Logged at Information.
        /// A partition NeoHub does not know is not created.
        /// </summary>
        public void ApplyAlarmMemory(string sessionId, PartitionAlarmMemory memory, ILogger log)
        {
            log.LogInformation("Alarm memory {MemoryState} on partition {Partition} (value 0x{Value:X2})",
                memory.InMemory ? "set" : "cleared", memory.Partition, memory.Value);

            UpdateKnownPartition(sessionId, memory.Partition, "alarm memory", log, (c, own) =>
            {
                own.AlarmMemory = memory.InMemory;
                if (memory.InMemory)
                    own.LastMemorySetAt = ++c.Sequence;
                else
                    own.LastMemoryClearAt = ++c.Sequence;
            });
        }

        /// <summary>
        /// An arm or disarm notification of <paramref name="partition"/> (which the caller may not have stored yet, and
        /// announces itself): its siren is turned off. Logged at Information when the siren was on.
        /// </summary>
        public void ClearSirenForArmDisarm(string sessionId, PartitionState partition, ILogger log)
        {
            var session = _panelState.GetSession(sessionId);
            if (session is null)
                return;

            var c = session.Conditions;
            lock (c.Gate)
            {
                var partitions = Snapshot(session, partition);
                var before = Capture(c, partitions);
                var own = c.Partitions[partition.PartitionNumber];
                if (own.SirenFromBell || own.SirenFromBit)
                    log.LogInformation("Siren cleared by arm/disarm on partition {Partition}", partition.PartitionNumber);
                own.SirenFromBell = false;
                own.SirenFromBit = false;
                own.LastSirenClearAt = ++c.Sequence;
                Publish(sessionId, session, partitions, before, partition, log);
            }
        }

        private void UpdateKnownPartition(string sessionId, int partitionNumber, string what, ILogger log,
            Action<PanelConditionsState, PartitionConditions> update)
        {
            var session = _panelState.GetSession(sessionId);
            PartitionState? partition = null;
            if (session is not null && partitionNumber is >= 1 and <= byte.MaxValue)
                session.Partitions.TryGetValue((byte)partitionNumber, out partition);
            if (session is null || partition is null)
            {
                log.LogInformation("Ignoring the {What} notification of partition {Partition}: not a known partition of the session", what, partitionNumber);
                return;
            }

            var c = session.Conditions;
            lock (c.Gate)
            {
                var partitions = Snapshot(session, null);
                var before = Capture(c, partitions);
                if (!c.Partitions.TryGetValue(partition.PartitionNumber, out var own))
                {
                    log.LogInformation("Ignoring the {What} notification of partition {Partition}: not a known partition of the session", what, partitionNumber);
                    return;
                }
                update(c, own);
                Publish(sessionId, session, partitions, before, null, log);
            }
        }

        #endregion

        #region Keypad alarms (0x0841)

        /// <summary>The keypad alarm type of a raw alarm type byte.</summary>
        public static string KeypadAlarmType(byte alarmType) =>
            alarmType == NonZoneAlarmStatusNotification.FireAlarmType ? FireKeypadAlarm : UnknownKeypadAlarm;

        /// <summary>
        /// Applies a parsed 0x0841 and logs it at Information. A state 01 is kept as the session's last keypad alarm and
        /// announced (<see cref="KeypadAlarm"/>), for any partition number; a state 00 is only logged. Partition states
        /// are not changed.
        /// </summary>
        public void ApplyKeypadAlarm(string sessionId, NonZoneAlarmStatus status, DateTime receivedAtUtc, ILogger log)
        {
            var type = KeypadAlarmType(status.AlarmType);
            log.LogInformation("Keypad alarm {AlarmType} (code {Code}) on partition {Partition} {AlarmState}",
                type, status.AlarmType, status.Partition, status.Active ? "active" : "restored");
            if (!status.Active)
                return;

            var alarm = new KeypadAlarmRecord(status.Partition, type, status.AlarmType, receivedAtUtc);
            GetOrCreateSession(sessionId).LastKeypadAlarm = alarm;
            KeypadAlarm?.Invoke(this, new KeypadAlarmEventArgs { SessionId = sessionId, Alarm = alarm });
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

        private readonly record struct Visible(bool Troubles, string TroubleTypes, bool Siren, bool AlarmMemory);

        private static Visible VisibleOf(PartitionState p) =>
            new(p.TroublesPresent, string.Join(",", p.TroubleTypes), p.SirenActive, p.AlarmsInMemory);

        /// <summary>The visible state of each partition, creating the per-partition entries first.</summary>
        private static Dictionary<PartitionState, Visible> Capture(PanelConditionsState c, List<PartitionState> partitions)
        {
            var before = new Dictionary<PartitionState, Visible>(ReferenceEqualityComparer.Instance);
            foreach (var p in partitions)
            {
                if (!c.Partitions.ContainsKey(p.PartitionNumber))
                    c.Partitions[p.PartitionNumber] = new PartitionConditions
                    {
                        ReadTroubles = p.TroublesPresent,
                        SirenFromBit = p.SirenActive,
                        AlarmMemory = p.AlarmsInMemory,
                    };
                before[p] = VisibleOf(p);
            }
            return before;
        }

        /// <summary>
        /// Recomputes every partition from the tracked state and announces the ones whose visible state changed, except
        /// <paramref name="pulled"/> (announced by its caller: the connect pull, whose changes are logged at Debug there,
        /// or an arm or disarm notification). Returns the partitions that changed.
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
                p.SirenActive = entry.SirenFromBell || entry.SirenFromBit;
                p.AlarmsInMemory = entry.AlarmMemory;
                if (VisibleOf(p) == old)
                    continue;

                changed.Add(p);
                if (ReferenceEquals(p, pulled))
                    continue;
                if (flag != old.Troubles)
                    log.LogInformation("Partition {Partition} troubles {Troubles}", p.PartitionNumber, flag ? "present" : "cleared");
                if (p.SirenActive != old.Siren)
                    log.LogInformation("Partition {Partition} siren {Siren}", p.PartitionNumber, p.SirenActive ? "on" : "off");
                if (p.AlarmsInMemory != old.AlarmMemory)
                    log.LogInformation("Partition {Partition} alarm memory {AlarmMemory}", p.PartitionNumber, p.AlarmsInMemory ? "set" : "cleared");
                // Never recreates a partition or session that was removed meanwhile.
                _panelState.TryUpdatePartition(sessionId, p);
            }
            return changed;
        }

        #endregion
    }
}
