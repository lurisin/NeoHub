using NeoHub.Services.PanelConfiguration;

namespace NeoHub.Services.Models
{
    /// <summary>
    /// Represents a complete session (panel connection) with its partitions and zones.
    /// </summary>
    public class SessionState
    {
        public required string SessionId { get; init; }
        public string Name => SessionId; // TODO: Allow friendly names
        public ConnectionPhase ConnectionPhase { get; set; }
        public int MaxZones { get; set; }
        public int MaxPartitions { get; set; }
        public int MaxUsers { get; set; }
        public Dictionary<byte, PartitionState> Partitions { get; } = new();
        public Dictionary<byte, ZoneState> Zones { get; } = new();

        /// <summary>
        /// Guards the structure of <see cref="Partitions"/> and <see cref="Zones"/>: <see cref="NeoHub.Services.PanelStateService"/>
        /// adds and looks up entries under it, and code that enumerates them away from the UI takes a copy under it
        /// (<see cref="CopyPartitions"/>, <see cref="CopyZones"/>). A Dictionary is not safe for a write concurrent with
        /// another write or with an enumeration. Never held while an event is raised.
        /// </summary>
        internal object CollectionsLock { get; } = new();

        /// <summary>A copy of <see cref="Partitions"/>, safe while partitions are being added.</summary>
        public Dictionary<byte, PartitionState> CopyPartitions()
        {
            lock (CollectionsLock)
                return new Dictionary<byte, PartitionState>(Partitions);
        }

        /// <summary>A copy of <see cref="Zones"/>, safe while zones are being added.</summary>
        public Dictionary<byte, ZoneState> CopyZones()
        {
            lock (CollectionsLock)
                return new Dictionary<byte, ZoneState>(Zones);
        }

        /// <summary>
        /// User slots and user-read operation state. Parallels <see cref="Configuration"/>.
        /// </summary>
        public PanelUserListState UserList { get; } = new();

        /// <summary>
        /// Installer configuration data read via SectionRead.
        /// Null until a config read is explicitly triggered by the user.
        /// </summary>
        public PanelConfigurationState? Configuration { get; set; }

        /// <summary>
        /// Whether the panel is currently in installer programming mode.
        /// Tracked via ProgrammingLeadInOut notifications from the panel.
        /// </summary>
        public bool IsInProgrammingMode { get; set; }

        /// <summary>
        /// Whether the initial configuration pull (capabilities, labels, status)
        /// has completed. User-initiated operations should wait for this.
        /// </summary>
        public bool IsInitialized { get; set; }

        /// <summary>
        /// Serializes panel configuration operations (reads and writes).
        /// Prevents the initial handler pull and user-initiated operations from interleaving.
        /// </summary>
        public SemaphoreSlim ConfigLock { get; } = new(1, 1);

        /// <summary>
        /// Trouble details and the derived trouble flags (<see cref="NeoHub.Services.PartitionConditionsService"/>).
        /// </summary>
        public PanelConditionsState Conditions { get; } = new();

        /// <summary>
        /// The last keypad alarm (ModuleStatus_Miscellaneous_NonZone_Alarm_Status with state 01) of this session, or
        /// null. Kept in memory only, with this state: across a takeover by a newer connection, not across a
        /// disconnect or a NeoHub restart.
        /// </summary>
        public KeypadAlarmRecord? LastKeypadAlarm { get; set; }

        /// <summary>
        /// When this session was established.
        /// </summary>
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the last message (send or receive) occurred on this session.
        /// </summary>
        public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The panel's reported date/time at the moment of the last broadcast.
        /// </summary>
        public DateTime? PanelDateTime { get; set; }

        /// <summary>
        /// The local UTC time when PanelDateTime was received, used to calculate a running clock offset.
        /// </summary>
        public DateTime? PanelDateTimeSyncedAt { get; set; }

        /// <summary>
        /// Returns the estimated current panel time by adding elapsed time since the last sync.
        /// </summary>
        public DateTime? PanelDateTimeNow =>
            PanelDateTime.HasValue && PanelDateTimeSyncedAt.HasValue
                ? PanelDateTime.Value + (DateTime.UtcNow - PanelDateTimeSyncedAt.Value)
                : null;

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}
