using System.Collections.Concurrent;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;
using Microsoft.Extensions.Options;
using NeoHub.Services.Settings;

namespace NeoHub.Services
{
    public class PanelCommandService : IPanelCommandService
    {
        private readonly IMediator _mediator;
        private readonly ILogger<PanelCommandService> _logger;
        private readonly IOptionsMonitor<PanelConnectionsSettings> _connectionSettings;

        public PanelCommandService(
            IMediator mediator, 
            ILogger<PanelCommandService> logger,
            IOptionsMonitor<PanelConnectionsSettings> connectionSettings)
        {
            _mediator = mediator;
            _logger = logger;
            _connectionSettings = connectionSettings;
        }

        private ConnectionSettings? GetConnection(string sessionId) =>
            _connectionSettings.CurrentValue.FindBySessionId(sessionId);

        /// <summary>Number of ArmAsync / DisarmAsync / BypassZoneAsync calls running, per session.</summary>
        private readonly ConcurrentDictionary<string, int> _commandsInProgress = new(StringComparer.OrdinalIgnoreCase);

        public bool IsCommandInProgress(string sessionId) =>
            _commandsInProgress.TryGetValue(sessionId ?? string.Empty, out var count) && count > 0;

        /// <summary>
        /// Counts one public command operation as in progress until disposed. A bypass (enter, write, exit)
        /// counts as one operation, so background reads see no gap between its steps.
        /// </summary>
        private CommandInProgress TrackCommand(string sessionId)
        {
            var key = sessionId ?? string.Empty;
            _commandsInProgress.AddOrUpdate(key, 1, (_, count) => count + 1);
            return new CommandInProgress(_commandsInProgress, key);
        }

        private readonly struct CommandInProgress : IDisposable
        {
            private readonly ConcurrentDictionary<string, int> _counts;
            private readonly string _key;

            public CommandInProgress(ConcurrentDictionary<string, int> counts, string key)
            {
                _counts = counts;
                _key = key;
            }

            public void Dispose() => _counts.AddOrUpdate(_key, 0, (_, count) => Math.Max(0, count - 1));
        }

        private const int NotReadyToArmResponseCode = 1;
        private const string NotReadyToArmMessage = "Partition not ready to arm";

        public async Task<PanelCommandResult> ArmAsync(string sessionId, byte partition, ArmingMode mode, string? accessCode = null)
        {
            using var inProgress = TrackCommand(sessionId);
            var conn = GetConnection(sessionId);
            var code = accessCode ?? conn?.DefaultAccessCode ?? string.Empty;

            _logger.LogInformation(
                "Arm command: Partition={Partition}, Mode={Mode}, UsingDefaultCode={UsingDefault}",
                partition, mode, string.IsNullOrEmpty(accessCode) && !string.IsNullOrEmpty(conn?.DefaultAccessCode));

            var message = new PartitionArm
            {
                Partition = partition,
                ArmMode = mode,
                AccessCode = code
            };

            var result = await SendCommandAsync(sessionId, message);

            // Response code 1 to an arm command means the partition is not ready to arm. One cause is an
            // open zone (seen on a PowerSeries Neo: rejected with code 1 while a door was open, accepted
            // once it was closed); others, such as a trouble that blocks arming, may give the same code,
            // so the text does not name a cause. ErrorMessage keeps the generic description of code 1.
            if (result.PanelResponseCode == NotReadyToArmResponseCode)
                result = result with { UserMessage = NotReadyToArmMessage };

            return result;
        }

        public async Task<PanelCommandResult> DisarmAsync(string sessionId, byte partition, string? accessCode = null)
        {
            using var inProgress = TrackCommand(sessionId);
            var conn = GetConnection(sessionId);
            var code = accessCode ?? conn?.DefaultAccessCode;

            if (string.IsNullOrEmpty(code))
            {
                return PanelCommandResult.Error("Access code is required to disarm");
            }

            _logger.LogInformation(
                "Disarm command: Partition={Partition}, UsingDefaultCode={UsingDefault}",
                partition, string.IsNullOrEmpty(accessCode) && !string.IsNullOrEmpty(conn?.DefaultAccessCode));

            var message = new PartitionDisarm
            {
                Partition = partition,
                AccessCode = code
            };

            return await SendCommandAsync(sessionId, message);
        }

        public async Task<PanelCommandResult> BypassZoneAsync(string sessionId, byte partition, byte zoneNumber, bool bypass, string? accessCode = null)
        {
            using var inProgress = TrackCommand(sessionId);
            var conn = GetConnection(sessionId);
            var code = accessCode ?? conn?.DefaultAccessCode;

            if (string.IsNullOrEmpty(code))
                return PanelCommandResult.Error("Access code is required to bypass zones");

            _logger.LogInformation(
                "Bypass command: Partition={Partition}, Zone={Zone}, Bypass={Bypass}, UsingDefaultCode={UsingDefault}",
                partition, zoneNumber, bypass,
                string.IsNullOrEmpty(accessCode) && !string.IsNullOrEmpty(conn?.DefaultAccessCode));

            var enterResult = await SendCommandAsync(sessionId, new ConfigurationEnter
            {
                Partition = partition,
                ProgrammingMode = ProgrammingMode.UserBypassProgramming,
                AccessCode = code,
                ReadWrite = ConfigurationEnter.ReadWriteAccessEnum.ReadWriteMode
            });

            if (!enterResult.Success)
            {
                _logger.LogWarning("Bypass failed: could not enter config mode. {Error}", enterResult.ErrorMessage);

                // Without an answer the panel may still have entered user bypass programming, so leave it,
                // as the finally block below does once the panel has confirmed the enter.
                if (enterResult.TimedOut)
                {
                    var exitResult = await SendCommandAsync(sessionId, new ConfigurationExit { Partition = partition });
                    if (!exitResult.Success)
                        _logger.LogWarning("Failed to exit config mode after the unanswered enter. {Error}", exitResult.ErrorMessage);
                }

                return enterResult;
            }

            PanelCommandResult bypassResult;
            try
            {
                bypassResult = await SendCommandAsync(sessionId, new SingleZoneBypassWrite
                {
                    Partition = partition,
                    ZoneNumber = zoneNumber,
                    BypassState = bypass ? BypassStatusEnum.Bypassed : BypassStatusEnum.NotBypassed,
                });
            }
            finally
            {
                var exitResult = await SendCommandAsync(sessionId, new ConfigurationExit { Partition = partition });
                if (!exitResult.Success)
                    _logger.LogWarning("Failed to exit config mode after bypass. {Error}", exitResult.ErrorMessage);
            }

            return bypassResult;
        }

        /// <summary>
        /// Longest wait for the panel's answer to one command sent by this service. The TLink receivers
        /// have no timeout of their own, so without this bound an unanswered command would wait forever.
        /// </summary>
        internal static readonly TimeSpan PanelAnswerTimeout = TimeSpan.FromSeconds(8);

        private static readonly string PanelAnswerTimeoutMessage =
            $"No confirmation from the panel within {PanelAnswerTimeout.TotalSeconds:0} s; check the partition state";

        private async Task<PanelCommandResult> SendCommandAsync(string sessionId, IMessageData message)
        {
            using var timeout = new CancellationTokenSource(PanelAnswerTimeout);
            try
            {
                // WaitAsync keeps the bound even if a lower layer does not observe the token.
                SessionResponse response = await _mediator.Send(new SessionCommand
                {
                    SessionID = sessionId,
                    MessageData = message
                }, timeout.Token).WaitAsync(timeout.Token);

                if (response.Success)
                    return response.PanelNack is null
                        ? PanelCommandResult.Ok()
                        : PanelCommandResult.Ok() with { PanelNack = response.PanelNack };

                // SessionMediator turns the cancellation into a failed response, so check our own token.
                if (timeout.IsCancellationRequested && !response.PanelResponseCode.HasValue)
                    return PanelAnswerTimedOut(message);

                _logger.LogWarning("Command failed: [{Code}] {Error}", response.ErrorCode, response.ErrorMessage);

                if (response.PanelResponseCode.HasValue)
                    return PanelCommandResult.Rejected(response.PanelResponseCode.Value, response.ErrorMessage ?? "Unknown error");

                return response.ErrorCode.HasValue
                    ? PanelCommandResult.Error(response.ErrorCode.Value, response.ErrorMessage ?? response.ErrorCode.Value.ToString())
                    : PanelCommandResult.Error(response.ErrorMessage ?? "Unknown error");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return PanelAnswerTimedOut(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending command");
                return PanelCommandResult.Error(ex.Message);
            }
        }

        private PanelCommandResult PanelAnswerTimedOut(IMessageData message)
        {
            // The panel may still act on the command; a late answer is dropped by the session.
            _logger.LogWarning("No answer from the panel to {Command} within {Seconds} s",
                message.GetType().Name, PanelAnswerTimeout.TotalSeconds);
            return PanelCommandResult.Error(PanelAnswerTimeoutMessage) with { TimedOut = true };
        }
    }
}
