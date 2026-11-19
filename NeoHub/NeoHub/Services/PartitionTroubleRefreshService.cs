using DSC.TLink.ITv2;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;
using Microsoft.Extensions.Options;
using NeoHub.Services.Models;
using NeoHub.Services.Settings;

namespace NeoHub.Services
{
    /// <summary>
    /// Keeps <see cref="PartitionState.TroublesPresent"/> current while a panel is connected.
    ///
    /// The flag is otherwise only read by the status pull when the session connects. For each connected
    /// session, once that pull has finished (<see cref="SessionState.IsInitialized"/>), this service re-reads
    /// <see cref="ModulePartitionStatus"/> for the session's partitions every
    /// <see cref="ConnectionSettings.TroubleRefreshIntervalSeconds"/> seconds (0 = off) and updates only
    /// <see cref="PartitionState.TroublesPresent"/> from it. Status, IsReady and the exit delay fields are never
    /// changed here. <see cref="IPanelStateService.UpdatePartition"/> is called only when the flag changes, so a
    /// read that finds no change sends nothing to clients.
    ///
    /// Reads are sent one at a time per session, are skipped while a <see cref="IPanelCommandService"/> command,
    /// a configuration operation or installer programming is in progress on that session, and wait at most as
    /// long as <see cref="PanelCommandService"/> does for the panel. A failed read keeps the last value.
    /// </summary>
    public sealed class PartitionTroubleRefreshService : IHostedService, IDisposable
    {
        /// <summary>Longest time between two checks of the interval setting, so a change applies without a restart.</summary>
        private static readonly TimeSpan SettingsRecheckInterval = TimeSpan.FromSeconds(5);

        private readonly IMediator _mediator;
        private readonly IPanelStateService _panelState;
        private readonly IPanelCommandService _commands;
        private readonly IITv2SessionManager _sessionManager;
        private readonly ISessionMonitor _sessionMonitor;
        private readonly IOptionsMonitor<PanelConnectionsSettings> _settings;
        private readonly ILogger<PartitionTroubleRefreshService> _logger;

        private readonly object _lock = new();
        private readonly Dictionary<string, SessionWorker> _workers = new();
        private bool _stopped;

        public PartitionTroubleRefreshService(
            IMediator mediator,
            IPanelStateService panelState,
            IPanelCommandService commands,
            IITv2SessionManager sessionManager,
            ISessionMonitor sessionMonitor,
            IOptionsMonitor<PanelConnectionsSettings> settings,
            ILogger<PartitionTroubleRefreshService> logger)
        {
            _mediator = mediator;
            _panelState = panelState;
            _commands = commands;
            _sessionManager = sessionManager;
            _sessionMonitor = sessionMonitor;
            _settings = settings;
            _logger = logger;

            _panelState.SessionStateChanged += OnSessionStateChanged;
            _sessionMonitor.SessionsChanged += OnSessionsChanged;
        }

        #region Lifecycle

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Sessions that finished their connect-time pull before this service was started.
            foreach (var (sessionId, session) in _panelState.GetAllSessions())
            {
                if (session.IsInitialized)
                    EnsureWorker(sessionId, session);
            }
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            List<SessionWorker> workers;
            lock (_lock)
            {
                _stopped = true;
                workers = _workers.Values.ToList();
                _workers.Clear();
            }

            foreach (var worker in workers)
                worker.Cancel();

            try
            {
                await Task.WhenAll(workers.Select(w => w.Completion)).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Host shutdown timeout; the workers have been cancelled and end on their own.
            }
        }

        public void Dispose()
        {
            _panelState.SessionStateChanged -= OnSessionStateChanged;
            _sessionMonitor.SessionsChanged -= OnSessionsChanged;

            lock (_lock)
            {
                _stopped = true;
                foreach (var worker in _workers.Values)
                    worker.Cancel();
                _workers.Clear();
            }
        }

        private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
        {
            // IsInitialized is set when the connect-time configuration and status pull has finished.
            if (e.Session.IsInitialized)
                EnsureWorker(e.SessionId, e.Session);
        }

        private void OnSessionsChanged()
        {
            List<SessionWorker> stopped = new();
            lock (_lock)
            {
                var active = _sessionManager.GetActiveSessions().ToHashSet();
                foreach (var (sessionId, worker) in _workers.ToList())
                {
                    if (active.Contains(sessionId))
                        continue;
                    _workers.Remove(sessionId);
                    stopped.Add(worker);
                }
            }

            foreach (var worker in stopped)
            {
                worker.Cancel();
                _logger.LogDebug("Trouble refresh stopped for session {SessionId}", worker.SessionId);
            }
        }

        /// <summary>Starts the worker for a session whose connect-time pull has finished, once per session state.</summary>
        private void EnsureWorker(string sessionId, SessionState session)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return;

            SessionWorker? replaced = null;
            SessionWorker worker;
            lock (_lock)
            {
                if (_stopped)
                    return;

                if (_workers.TryGetValue(sessionId, out var existing) && ReferenceEquals(existing.State, session))
                    return;

                if (!_sessionManager.GetActiveSessions().Contains(sessionId))
                    return;

                replaced = existing;
                worker = new SessionWorker(sessionId, session);
                _workers[sessionId] = worker;
                worker.Completion = Task.Run(() => RunAsync(worker));
            }

            replaced?.Cancel();
            _logger.LogDebug("Trouble refresh started for session {SessionId}", sessionId);
        }

        #endregion

        #region Worker loop

        private async Task RunAsync(SessionWorker worker)
        {
            var ct = worker.Token;
            long? nextRoundAt = null;   // Environment.TickCount64, so clock changes do not matter

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var intervalMs = GetIntervalSeconds(worker.SessionId) * 1000L;
                    var now = Environment.TickCount64;

                    if (intervalMs <= 0)
                        nextRoundAt = null;
                    else if (nextRoundAt is null || nextRoundAt > now + intervalMs)
                        nextRoundAt = now + intervalMs;   // first round one interval after the connect-time pull

                    if (nextRoundAt is long due && now >= due)
                    {
                        await RunRoundAsync(worker, ct);
                        nextRoundAt = Environment.TickCount64 + intervalMs;
                        continue;
                    }

                    var waitMs = nextRoundAt is long next
                        ? Math.Min(next - now, (long)SettingsRecheckInterval.TotalMilliseconds)
                        : (long)SettingsRecheckInterval.TotalMilliseconds;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, waitMs)), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let the loop die; try again after a pause.
                    _logger.LogDebug(ex, "Trouble refresh error for session {SessionId}", worker.SessionId);
                    try { await Task.Delay(SettingsRecheckInterval, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task RunRoundAsync(SessionWorker worker, CancellationToken ct)
        {
            byte[] partitions;
            try
            {
                partitions = _panelState.GetPartitions(worker.SessionId).Keys.OrderBy(p => p).ToArray();
            }
            catch (InvalidOperationException)
            {
                // The partition dictionary changed while it was copied; the next round reads it again.
                return;
            }

            foreach (var partition in partitions)
            {
                ct.ThrowIfCancellationRequested();
                await RefreshPartitionAsync(worker, partition, ct);
            }
        }

        private async Task RefreshPartitionAsync(SessionWorker worker, byte partition, CancellationToken ct)
        {
            // Checked before each partition, so a command that starts during a round skips the rest of it.
            if (!CanRead(worker, out var reason))
            {
                _logger.LogDebug("Trouble refresh of partition {Partition} skipped: {Reason}", partition, reason);
                return;
            }

            var (troubles, error) = await ReadTroublesAsync(worker.SessionId, partition, ct);
            if (troubles is not bool present)
            {
                ReportFailure(worker, partition, error!);
                return;
            }

            ReportSuccess(worker);
            ApplyTroubles(worker, partition, present);
        }

        private bool CanRead(SessionWorker worker, out string reason)
        {
            var session = _panelState.GetSession(worker.SessionId);
            if (!_sessionManager.GetActiveSessions().Contains(worker.SessionId) || session is null)
                reason = "session not connected";
            else if (!ReferenceEquals(session, worker.State) || !session.IsInitialized)
                reason = "connect-time status pull not finished";
            else if (session.ConfigLock.CurrentCount == 0)
                reason = "configuration operation in progress";
            else if (session.IsInProgrammingMode)
                reason = "panel in programming mode";
            else if (_commands.IsCommandInProgress(worker.SessionId))
                reason = "command waiting for the panel";
            else
            {
                reason = string.Empty;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reads PartitionStatus2.TroublesPresent for one partition. Returns the flag, or null and the reason
        /// when there is no usable answer within <see cref="PanelCommandService.PanelAnswerTimeout"/>.
        /// </summary>
        private async Task<(bool? Troubles, string? Error)> ReadTroublesAsync(string sessionId, byte partition, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PanelCommandService.PanelAnswerTimeout);
            try
            {
                // WaitAsync keeps the bound even if a lower layer does not observe the token.
                var response = await _mediator.Send(new SessionCommand
                {
                    SessionID = sessionId,
                    MessageData = new CommandRequestMessage { Request = new ModulePartitionStatus { Partition = partition } },
                    LogFailuresAtDebug = true
                }, timeout.Token).WaitAsync(timeout.Token);

                // SessionMediator turns a cancellation into a failed response, so check the tokens.
                ct.ThrowIfCancellationRequested();
                if (!response.Success)
                    return (null, timeout.IsCancellationRequested ? NoAnswerText : response.ErrorMessage ?? "request failed");
                if (response.PanelNack is not null)
                    return (null, $"refused by the panel interface (NACK: {response.PanelNack})");
                if (response.MessageData is not ModulePartitionStatus status)
                    return (null, $"unexpected answer {response.MessageData?.GetType().Name ?? "(none)"}");
                if (status.Partition != partition)
                    return (null, $"answer for partition {status.Partition}");
                if (status.PartitionStatus.Length < 2)
                    return (null, "answer without the second status byte");

                return (status.Status2.HasFlag(ModulePartitionStatus.PartitionStatus2.TroublesPresent), null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, NoAnswerText);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, ex.Message);
            }
        }

        private static readonly string NoAnswerText =
            $"no answer within {PanelCommandService.PanelAnswerTimeout.TotalSeconds:0} s";

        /// <summary>Sets only TroublesPresent, and only broadcasts (through UpdatePartition) when it changed.</summary>
        private void ApplyTroubles(SessionWorker worker, byte partition, bool troubles)
        {
            // Re-check after the await: never create a session or partition from here.
            if (!ReferenceEquals(_panelState.GetSession(worker.SessionId), worker.State))
                return;

            var state = _panelState.GetPartition(worker.SessionId, partition);
            if (state is null || state.TroublesPresent == troubles)
                return;

            state.TroublesPresent = troubles;
            _logger.LogInformation("Partition {Partition} troubles {Troubles}",
                partition, troubles ? "present" : "cleared");
            _panelState.UpdatePartition(worker.SessionId, state);
        }

        private void ReportFailure(SessionWorker worker, byte partition, string error)
        {
            bool warn;
            lock (worker.Gate)
            {
                warn = !worker.FailureWarned;
                worker.FailureWarned = true;
            }

            if (warn)
                _logger.LogWarning(
                    "Could not read the trouble status of partition {Partition}: {Error}. Keeping the last value; further failures are logged at Debug until a read succeeds",
                    partition, error);
            else
                _logger.LogDebug("Could not read the trouble status of partition {Partition}: {Error}", partition, error);
        }

        private void ReportSuccess(SessionWorker worker)
        {
            bool recovered;
            lock (worker.Gate)
            {
                recovered = worker.FailureWarned;
                worker.FailureWarned = false;
            }

            if (recovered)
                _logger.LogInformation("Trouble status reads work again");
        }

        private int GetIntervalSeconds(string sessionId) =>
            _settings.CurrentValue.FindBySessionId(sessionId)?.TroubleRefreshIntervalSeconds
                ?? ConnectionSettings.DefaultTroubleRefreshIntervalSeconds;

        #endregion

        private sealed class SessionWorker
        {
            private readonly CancellationTokenSource _cts = new();

            public SessionWorker(string sessionId, SessionState state)
            {
                SessionId = sessionId;
                State = state;
                Token = _cts.Token;
            }

            public string SessionId { get; }

            /// <summary>The session state this worker was started for; a reconnect creates a new one.</summary>
            public SessionState State { get; }

            public CancellationToken Token { get; }
            public Task Completion { get; set; } = Task.CompletedTask;
            public object Gate { get; } = new();
            public bool FailureWarned { get; set; }

            public void Cancel()
            {
                try { _cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }
}
