using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;
using NeoHub.Services.Models;

namespace NeoHub.Services.Handlers
{
    /// <summary>
    /// Handles arm/disarm notifications from the panel and updates partition state. An arm or disarm also turns the
    /// partition's siren off (<see cref="PartitionConditionsService.ClearSirenForArmDisarm"/>), in the same update.
    /// </summary>
    public class ArmDisarmNotificationHandler
        : INotificationHandler<SessionNotification<NotificationArmDisarm>>
    {
        private readonly IPanelStateService _service;
        private readonly ILogger<ArmDisarmNotificationHandler> _logger;
        private readonly PartitionConditionsService? _conditions;

        /// <summary>Without the conditions service: the siren is not cleared by an arm or disarm.</summary>
        public ArmDisarmNotificationHandler(
            IPanelStateService service,
            ILogger<ArmDisarmNotificationHandler> logger)
            : this(service, logger, conditions: null)
        {
        }

        public ArmDisarmNotificationHandler(
            IPanelStateService service,
            ILogger<ArmDisarmNotificationHandler> logger,
            PartitionConditionsService? conditions)
        {
            _service = service;
            _logger = logger;
            _conditions = conditions;
        }

        public Task Handle(
            SessionNotification<NotificationArmDisarm> notification,
            CancellationToken cancellationToken)
        {
            var msg = notification.MessageData;
            var sessionId = notification.SessionId;

            _logger.LogInformation(
                "Partition {Partition} {ArmMode} via {Method} by user {UserId} (Session: {SessionId})",
                msg.Partition, msg.ArmMode, msg.Method, msg.UserId, sessionId);

            var partition = _service.GetPartition(sessionId, (byte)msg.Partition)
                ?? new PartitionState { PartitionNumber = (byte)msg.Partition };

            partition.Status = msg.ArmMode switch
            {
                ArmingMode.Disarm => PartitionStatus.Disarmed,
                ArmingMode.AwayArm or ArmingMode.AwayArmWithNoEntryDelay => PartitionStatus.ArmedAway,
                ArmingMode.StayArm or ArmingMode.StayArmWithNoEntryDelay => PartitionStatus.ArmedHome,
                ArmingMode.NightArm or ArmingMode.NightArmWithNoEntryDelay => PartitionStatus.ArmedNight,
                ArmingMode.ArmWithNoEntryDelay => PartitionStatus.ArmedAway,
                _ => PartitionStatus.ArmedAway
            };

            // Clear exit delay state when disarming
            if (msg.ArmMode == ArmingMode.Disarm)
            {
                partition.ExitDelayActive = false;
                partition.ExitDelayStartedAt = null;
                partition.ExitDelayDurationSeconds = 0;
                partition.ExitDelayAudible = false;
                partition.ExitDelayUrgent = false;
            }

            partition.LastUpdated = notification.ReceivedAt;

            // A missed "bell off" must not hide the next alarm: any arm or disarm turns the siren off. Whatever
            // happens there, the arm or disarm itself is still stored and announced.
            try
            {
                _conditions?.ClearSirenForArmDisarm(sessionId, partition, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not clear the siren of partition {Partition} for the arm or disarm", msg.Partition);
            }

            _service.UpdatePartition(sessionId, partition);

            return Task.CompletedTask;
        }
    }
}
