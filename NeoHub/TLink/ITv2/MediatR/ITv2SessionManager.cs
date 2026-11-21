using System.Collections.Concurrent;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.MediatR
{
    /// <summary>
    /// Manages active ITv2 session instances and routes commands to the correct session.
    /// </summary>
    public interface IITv2SessionManager
    {
        internal void RegisterSession(string sessionId, IITv2Session session);
        internal void UnregisterSession(string sessionId, IITv2Session session);
        internal bool IsCurrentSession(string sessionId, IITv2Session session);
        internal IITv2Session? GetSession(string sessionId);
        IEnumerable<string> GetActiveSessions();
        Task DisconnectSessionAsync(string sessionId);
        Task DisconnectAllAsync();
    }

    /// <summary>
    /// One registration per session id, owned by the newest connection.
    ///
    /// A panel can reconnect before NeoHub has noticed that its previous connection is gone. The new connection
    /// then takes over the registration (<see cref="RegisterSession"/>), the old session object is closed without
    /// END_SESSION, and only the connection that still owns the registration can remove it
    /// (<see cref="UnregisterSession"/>), so the teardown of the old connection leaves the new one alone.
    ///
    /// Lifecycle notifications are published one at a time per session id, in the order of the registration
    /// changes that caused them: each waits until the handlers of the previous one have finished (the
    /// Connected handlers include the connect-time pull). So a Disconnected of an old connection always
    /// completes before the Connected of a newer one starts.
    /// </summary>
    internal class ITv2SessionManager : IITv2SessionManager
    {
        private readonly ConcurrentDictionary<string, IITv2Session> _sessions = new();
        private readonly IMediator _mediator;
        private readonly ILogger<ITv2SessionManager> _logger;

        // Guards registration changes together with queuing their lifecycle notification, so the queue order is
        // the order of the changes. Reads (GetSession, GetActiveSessions) do not take it.
        private readonly object _gate = new();

        // Last queued lifecycle publication per session id (guarded by _gate); removed once it has run and
        // nothing was queued after it.
        private readonly Dictionary<string, Task> _lifecycleTails = new();

        public ITv2SessionManager(IMediator mediator, ILogger<ITv2SessionManager> logger)
        {
            _mediator = mediator;
            _logger = logger;
        }

        public void RegisterSession(string sessionId, IITv2Session session)
        {
            IITv2Session? replaced;
            lock (_gate)
            {
                if (_sessions.TryGetValue(sessionId, out replaced) && ReferenceEquals(replaced, session))
                {
                    _logger.LogWarning("Session already registered");
                    return;
                }

                _sessions[sessionId] = session;
                QueueLifecycleNotification(sessionId,
                    new SessionConnectedNotification(sessionId) { ReplacedConnection = replaced is not null });
            }

            if (replaced is null)
            {
                _logger.LogInformation("Session registered. Active sessions: {Count}", _sessions.Count);
                return;
            }

            // The panel reconnected before its previous connection was torn down. Commands now go to the new
            // connection; the old one is closed so its loop ends, and its teardown finds nothing to remove.
            _logger.LogWarning("Session replaced by a newer connection. Active sessions: {Count}", _sessions.Count);
            _ = CloseReplacedSessionAsync(replaced);
        }

        public void UnregisterSession(string sessionId, IITv2Session session)
        {
            lock (_gate)
            {
                // Compare-and-remove: only the connection that still owns the registration removes it.
                if (!_sessions.TryRemove(new KeyValuePair<string, IITv2Session>(sessionId, session)))
                {
                    _logger.LogDebug("Connection ended after its registration was replaced or removed; nothing to unregister");
                    return;
                }
                QueueLifecycleNotification(sessionId, new SessionDisconnectedNotification(sessionId));
            }

            _logger.LogInformation("Session unregistered. Active sessions: {Count}", _sessions.Count);
        }

        public bool IsCurrentSession(string sessionId, IITv2Session session) =>
            _sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, session);

        public IITv2Session? GetSession(string sessionId)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }

        public async Task DisconnectSessionAsync(string sessionId)
        {
            IITv2Session? session;
            lock (_gate)
            {
                if (!_sessions.TryRemove(sessionId, out session))
                    return;
                // Queued now, not after the dispose below (which can take seconds), so a reconnect in the meantime
                // gets its Connected after this Disconnected.
                QueueLifecycleNotification(sessionId, new SessionDisconnectedNotification(sessionId));
            }

            _logger.LogInformation("Disconnecting session {SessionId}", sessionId);
            await session.DisposeAsync();
        }

        public async Task DisconnectAllAsync()
        {
            var sessionIds = _sessions.Keys.ToList();
            if (sessionIds.Count == 0)
                return;

            _logger.LogInformation("Disconnecting all {Count} active session(s)", sessionIds.Count);
            await Task.WhenAll(sessionIds.Select(DisconnectSessionAsync));
        }

        public IEnumerable<string> GetActiveSessions()
        {
            return _sessions.Keys.ToList();
        }

        /// <summary>Lifecycle publications still queued or running (tests).</summary>
        internal int PendingLifecycleCount
        {
            get { lock (_gate) return _lifecycleTails.Count; }
        }

        /// <summary>Completes when the lifecycle notifications queued so far for the session id have been handled (tests).</summary>
        internal Task LifecycleIdleAsync(string sessionId)
        {
            lock (_gate)
                return _lifecycleTails.TryGetValue(sessionId, out var tail) ? tail : Task.CompletedTask;
        }

        private async Task CloseReplacedSessionAsync(IITv2Session replaced)
        {
            try
            {
                await replaced.AbandonAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing the replaced connection");
            }
        }

        /// <summary>
        /// Queues a lifecycle notification behind the previous one for the same session id. Called under _gate.
        /// The publication always runs on the thread pool, never inline, so no handler runs under the lock.
        /// </summary>
        private void QueueLifecycleNotification(string sessionId, INotification notification)
        {
            var previous = _lifecycleTails.TryGetValue(sessionId, out var tail) ? tail : Task.CompletedTask;
            var next = previous.ContinueWith(
                _ => PublishLifecycleNotificationAsync(notification),
                CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
            _lifecycleTails[sessionId] = next;

            next.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    if (_lifecycleTails.TryGetValue(sessionId, out var current) && ReferenceEquals(current, next))
                        _lifecycleTails.Remove(sessionId);
                }
            }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        private async Task PublishLifecycleNotificationAsync(INotification notification)
        {
            try
            {
                await _mediator.Publish(notification);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing session lifecycle notification");
            }
        }
    }
}