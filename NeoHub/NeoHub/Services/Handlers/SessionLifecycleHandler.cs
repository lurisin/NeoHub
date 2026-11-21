using DSC.TLink.ITv2;
using DSC.TLink.ITv2.MediatR;
using MediatR;

namespace NeoHub.Services.Handlers
{
    /// <summary>
    /// Relays MediatR session lifecycle notifications to the UI event service.
    /// </summary>
    public class SessionLifecycleHandler :
        INotificationHandler<SessionConnectedNotification>,
        INotificationHandler<SessionDisconnectedNotification>
    {
        private readonly ISessionMonitor _monitor;
        private readonly IPanelStateService _panelState;
        private readonly IConnectionSettingsProvider _settingsProvider;
        private readonly ILogger<SessionLifecycleHandler> _logger;

        public SessionLifecycleHandler(
            ISessionMonitor monitor,
            IPanelStateService panelState,
            IConnectionSettingsProvider settingsProvider,
            ILogger<SessionLifecycleHandler> logger)
        {
            _monitor = monitor;
            _panelState = panelState;
            _settingsProvider = settingsProvider;
            _logger = logger;
        }

        public Task Handle(SessionConnectedNotification notification, CancellationToken cancellationToken)
        {
            if (notification.ReplacedConnection)
            {
                // A newer connection took the session over. Its state is kept, so clients see no gap; the connect
                // pull that follows refreshes it. What belonged to the old connection starts afresh, as it does for
                // a new state: the programming mode flag (set by lead-in / lead-out notifications on the old
                // connection; a missed lead-out would otherwise pause the trouble refresh) and the connect time.
                _logger.LogInformation("Session taken over by a newer connection; keeping its state until the connect pull refreshes it");
                if (_panelState.GetSession(notification.SessionId) is not null)
                {
                    _panelState.UpdateSession(notification.SessionId, s =>
                    {
                        s.IsInProgrammingMode = false;
                        s.ConnectedAt = DateTime.UtcNow;
                    });
                }
            }
            else
            {
                _logger.LogInformation("Session connected");
            }
            _settingsProvider.ConfirmDefaults(notification.SessionId);
            _monitor.NotifyChanged();
            return Task.CompletedTask;
        }

        public Task Handle(SessionDisconnectedNotification notification, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Session disconnected");
            _panelState.RemoveSession(notification.SessionId);
            _monitor.NotifyChanged();
            return Task.CompletedTask;
        }
    }
}