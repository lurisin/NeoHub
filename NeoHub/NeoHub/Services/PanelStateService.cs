using System.Collections.Concurrent;
using NeoHub.Services.Models;

namespace NeoHub.Services
{
    public class PanelStateService : IPanelStateService
    {
        private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
        private readonly ILogger<PanelStateService> _logger;

        public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;
        public event EventHandler<PartitionStateChangedEventArgs>? PartitionStateChanged;
        public event EventHandler<ZoneStateChangedEventArgs>? ZoneStateChanged;
        public event EventHandler<ConfigurationCompleteEventArgs>? ConfigurationComplete;

        public PanelStateService(ILogger<PanelStateService> logger)
        {
            _logger = logger;
        }

        #region Session Operations

        public SessionState? GetSession(string sessionId)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }

        public IReadOnlyDictionary<string, SessionState> GetAllSessions()
        {
            return _sessions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        private SessionState GetOrCreateSession(string sessionId)
        {
            return _sessions.GetOrAdd(sessionId, id => new SessionState { SessionId = id });
        }

        public void UpdateSession(string sessionId, Action<SessionState> update)
        {
            var session = GetOrCreateSession(sessionId);
            update(session);
            session.LastUpdated = DateTime.UtcNow;
            session.LastMessageAt = DateTime.UtcNow;

            _logger.LogDebug("Updated session {Session}", sessionId);

            SessionStateChanged?.Invoke(this, new SessionStateChangedEventArgs
            {
                SessionId = sessionId,
                Session = session
            });
        }

        public void RemoveSession(string sessionId)
        {
            if (_sessions.TryRemove(sessionId, out _))
                _logger.LogInformation("Removed session state for {Session}", sessionId);
        }

        #endregion

        #region Partition Operations

        public PartitionState? GetPartition(string sessionId, byte partitionNumber)
        {
            var session = GetSession(sessionId);
            if (session is null)
                return null;
            lock (session.CollectionsLock)
                return session.Partitions.TryGetValue(partitionNumber, out var partition) ? partition : null;
        }

        /// <summary>A copy of the session's partitions (empty without the session), safe while partitions are added.</summary>
        public IReadOnlyDictionary<byte, PartitionState> GetPartitions(string sessionId)
        {
            var session = GetSession(sessionId);
            return session?.CopyPartitions() ?? new Dictionary<byte, PartitionState>();
        }

        public void UpdatePartition(string sessionId, PartitionState partition)
        {
            var session = GetOrCreateSession(sessionId);
            lock (session.CollectionsLock)
                session.Partitions[partition.PartitionNumber] = partition;
            PublishPartitionUpdate(sessionId, session, partition);
        }

        public bool TryUpdatePartition(string sessionId, PartitionState partition)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return false;
            PartitionState? stored;
            lock (session.CollectionsLock)
                session.Partitions.TryGetValue(partition.PartitionNumber, out stored);
            if (!ReferenceEquals(stored, partition))
                return false;

            PublishPartitionUpdate(sessionId, session, partition);
            return true;
        }

        private void PublishPartitionUpdate(string sessionId, SessionState session, PartitionState partition)
        {
            session.LastUpdated = DateTime.UtcNow;
            session.LastMessageAt = DateTime.UtcNow;

            _logger.LogDebug("Updated partition {Partition} for session {Session}",
                partition.PartitionNumber, sessionId);

            PartitionStateChanged?.Invoke(this, new PartitionStateChangedEventArgs
            {
                SessionId = sessionId,
                Partition = partition
            });
        }

        #endregion

        #region Zone Operations

        public ZoneState? GetZone(string sessionId, byte zoneNumber)
        {
            var session = GetSession(sessionId);
            if (session is null)
                return null;
            lock (session.CollectionsLock)
                return session.Zones.TryGetValue(zoneNumber, out var zone) ? zone : null;
        }

        /// <summary>A copy of the session's zones (empty without the session), safe while zones are added.</summary>
        public IReadOnlyDictionary<byte, ZoneState> GetZones(string sessionId)
        {
            var session = GetSession(sessionId);
            return session?.CopyZones() ?? new Dictionary<byte, ZoneState>();
        }

        public void UpdateZone(string sessionId, ZoneState zone)
        {
            var session = GetOrCreateSession(sessionId);
            lock (session.CollectionsLock)
                session.Zones[zone.ZoneNumber] = zone;
            session.LastUpdated = DateTime.UtcNow;
            session.LastMessageAt = DateTime.UtcNow;

            _logger.LogDebug("Updated zone {Zone} for session {Session}",
                zone.ZoneNumber, sessionId);

            ZoneStateChanged?.Invoke(this, new ZoneStateChangedEventArgs
            {
                SessionId = sessionId,
                Zone = zone
            });
        }

        #endregion

        #region Configuration Complete

        public void OnConfigurationComplete(string sessionId)
        {
            _logger.LogInformation("Configuration complete");
            ConfigurationComplete?.Invoke(this, new ConfigurationCompleteEventArgs { SessionId = sessionId });
        }

        #endregion
    }
}