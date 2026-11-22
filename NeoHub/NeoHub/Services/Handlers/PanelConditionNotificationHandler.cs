using DSC.TLink.Extensions;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;

namespace NeoHub.Services.Handlers
{
    /// <summary>
    /// Handles the panel's trouble detail notifications (0x0823), keypad alarms (0x0841), audible bell (0x023B) and
    /// alarm memory (0x023C) notifications through <see cref="PartitionConditionsService"/>. A payload that does not
    /// fit the inferred layout is logged at Information with its hex and ignored.
    /// </summary>
    public class PanelConditionNotificationHandler
        : INotificationHandler<SessionNotification<TroubleDetailNotification>>,
          INotificationHandler<SessionNotification<NonZoneAlarmStatusNotification>>,
          INotificationHandler<SessionNotification<PartitionAudibleBellNotification>>,
          INotificationHandler<SessionNotification<PartitionAlarmMemoryNotification>>
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
            if (!message.TryParse(out var details))
            {
                _logger.LogInformation(
                    "Ignoring a ModuleStatus_Trouble_Detail_Notification that does not fit the expected layout: {Payload}",
                    new HexBytes(message.Payload));
                return Task.CompletedTask;
            }

            _logger.LogDebug("ModuleStatus_Trouble_Detail_Notification payload {Payload}{Burst}",
                new HexBytes(message.Payload), message.ReceivedInConnectBurst ? " (connect burst)" : string.Empty);
            _conditions.ApplyTroubleDetails(notification.SessionId, details, message.ReceivedInConnectBurst, _logger);
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

        public Task Handle(SessionNotification<PartitionAudibleBellNotification> notification, CancellationToken cancellationToken)
        {
            var message = notification.MessageData;
            if (!message.TryParse(out var bell))
            {
                _logger.LogInformation(
                    "Ignoring a Notification_Partition_Audible_Bell that does not fit the expected layout: {Payload}",
                    new HexBytes(message.Payload));
                return Task.CompletedTask;
            }

            _logger.LogDebug("Notification_Partition_Audible_Bell payload {Payload}", new HexBytes(message.Payload));
            _conditions.ApplyAudibleBell(notification.SessionId, bell, _logger);
            return Task.CompletedTask;
        }

        public Task Handle(SessionNotification<PartitionAlarmMemoryNotification> notification, CancellationToken cancellationToken)
        {
            var message = notification.MessageData;
            if (!message.TryParse(out var memory))
            {
                _logger.LogInformation(
                    "Ignoring a Notification_Partition_Alarm_Memory that does not fit the expected layout: {Payload}",
                    new HexBytes(message.Payload));
                return Task.CompletedTask;
            }

            _logger.LogDebug("Notification_Partition_Alarm_Memory payload {Payload}", new HexBytes(message.Payload));
            _conditions.ApplyAlarmMemory(notification.SessionId, memory, _logger);
            return Task.CompletedTask;
        }
    }
}
