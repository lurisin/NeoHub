using DSC.TLink.Extensions;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;

namespace NeoHub.Services.Handlers
{
    /// <summary>
    /// Handles the panel's trouble detail notifications (0x0823) and keypad alarms (0x0841) through
    /// <see cref="PartitionConditionsService"/>. A payload that does not fit the inferred layout is logged at
    /// Information with its hex and ignored.
    /// </summary>
    public class PanelConditionNotificationHandler
        : INotificationHandler<SessionNotification<TroubleDetailNotification>>,
          INotificationHandler<SessionNotification<NonZoneAlarmStatusNotification>>
    {
        private readonly PartitionConditionsService _conditions;
        private readonly ILogger<PanelConditionNotificationHandler> _logger;

        public PanelConditionNotificationHandler(
            PartitionConditionsService conditions,
            ILogger<PanelConditionNotificationHandler> logger)
        {
            _conditions = conditions;
            _logger = logger;
        }

        public Task Handle(SessionNotification<TroubleDetailNotification> notification, CancellationToken cancellationToken)
        {
            var message = notification.MessageData;
            if (!message.TryParse(out var detail))
            {
                _logger.LogInformation(
                    "Ignoring a ModuleStatus_Trouble_Detail_Notification that does not fit the expected layout: {Payload}",
                    new HexBytes(message.Payload));
                return Task.CompletedTask;
            }

            _logger.LogDebug("ModuleStatus_Trouble_Detail_Notification payload {Payload}{Burst}",
                new HexBytes(message.Payload), message.ReceivedInConnectBurst ? " (connect burst)" : string.Empty);
            _conditions.ApplyTroubleDetail(notification.SessionId, detail, message.ReceivedInConnectBurst, _logger);
            return Task.CompletedTask;
        }

        public Task Handle(SessionNotification<NonZoneAlarmStatusNotification> notification, CancellationToken cancellationToken)
        {
            var message = notification.MessageData;
            if (!message.TryParse(out var status))
            {
                _logger.LogInformation(
                    "Ignoring a ModuleStatus_Miscellaneous_NonZone_Alarm_Status that does not fit the expected layout: {Payload}",
                    new HexBytes(message.Payload));
                return Task.CompletedTask;
            }

            _logger.LogDebug("ModuleStatus_Miscellaneous_NonZone_Alarm_Status payload {Payload}", new HexBytes(message.Payload));
            _conditions.ApplyKeypadAlarm(notification.SessionId, status, notification.ReceivedAt, _logger);
            return Task.CompletedTask;
        }
    }
}
