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
        private readonly IFapCommandSettings? _fapSettings;

        /// <summary>Without FAP settings: <see cref="ActivateFapAsync"/> always refuses.</summary>
        public PanelCommandService(
            IMediator mediator, 
            ILogger<PanelCommandService> logger,
            IOptionsMonitor<PanelConnectionsSettings> connectionSettings)
            : this(mediator, logger, connectionSettings, fapSettings: null)
        {
        }

        public PanelCommandService(
            IMediator mediator,
            ILogger<PanelCommandService> logger,
            IOptionsMonitor<PanelConnectionsSettings> connectionSettings,
            IFapCommandSettings? fapSettings)
        {
            _mediator = mediator;
            _logger = logger;
            _connectionSettings = connectionSettings;
            _fapSettings = fapSettings;
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

            public void Dispose()
            {
                _counts.AddOrUpdate(_key, 0, (_, count) => Math.Max(0, count - 1));
                // Drop the entry once nothing runs, so session ids sent by clients (also unknown ones) leave
                // nothing behind. Removes only if still 0: a command that started meanwhile keeps its count.
                _counts.TryRemove(new KeyValuePair<string, int>(_key, 0));
            }
        }

        private const int NotReadyToArmResponseCode = 1;

        public event EventHandler<ArmRejectedNotReadyEventArgs>? ArmRejectedNotReady;

        public async Task<PanelCommandResult> ArmAsync(string sessionId, byte partition, ArmingMode mode, string? accessCode = null)
        {
            PanelCommandResult result;
            using (TrackCommand(sessionId))
                result = await ArmCoreAsync(sessionId, partition, mode, accessCode);

            // Raised once the command no longer counts as in progress, so a read it triggers is not skipped
            // because of this command. Handlers only queue work; the result is returned right away.
            if (result.PanelResponseCode == NotReadyToArmResponseCode)
                RaiseArmRejectedNotReady(sessionId, partition);

            return result;
        }

        private void RaiseArmRejectedNotReady(string sessionId, byte partition)
        {
            try
            {
                ArmRejectedNotReady?.Invoke(this, new ArmRejectedNotReadyEventArgs { SessionId = sessionId, Partition = partition });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in an arm rejection handler");
            }
        }

        private async Task<PanelCommandResult> ArmCoreAsync(string sessionId, byte partition, ArmingMode mode, string? accessCode)
        {
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

            // Response code 1 to an arm command means the partition is not ready to arm. One cause is an
            // open zone (seen on a PowerSeries Neo: rejected with code 1 while a door was open, accepted
            // once it was closed); others, such as a trouble that blocks arming, may give the same code,
            // so the text does not name a cause (PanelRejectionText.ForResponseCode). ErrorMessage keeps the
            // generic description of code 1.
            return await SendCommandAsync(sessionId, message);
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
        /// The reply when a FAP command got no usable answer (no answer in time, or the session closed while it
        /// waited): the panel may have raised the alarm, so nobody should resend it thinking nothing happened.
        /// </summary>
        public const string FapNoAnswerMessage = "No answer from the panel: the alarm may still have been triggered";

        /// <summary>
        /// The reply when a FAP command failed before it was written to the panel (the session was not ready in time,
        /// or ended first): it was not sent, and it cannot be sent later.
        /// </summary>
        public const string FapNotSentMessage = "The FAP command was not sent: the panel session was not ready, or ended, before it could be sent";

        public async Task<PanelCommandResult> ActivateFapAsync(string sessionId, byte partition, FapType type)
        {
            using var inProgress = TrackCommand(sessionId);

            if (_fapSettings?.AllowFapCommands != true)
            {
                _logger.LogWarning("FAP command not sent: {Reason}", ActivateFap.DisabledMessage);
                return PanelCommandResult.Error(ActivateFap.DisabledMessage) with { NotSent = true };
            }

            // The WebSocket handler validates the request; this guards other callers. Never a default type or partition.
            if (partition < 1 || !Enum.IsDefined(type))
            {
                _logger.LogWarning("FAP command not sent: invalid partition {Partition} or type {Type}", partition, (byte)type);
                return PanelCommandResult.Error("Invalid FAP request") with { NotSent = true };
            }

            _logger.LogWarning(
                "FAP command (experimental, raises a real alarm): Partition={Partition}, Type={Type}; sent once, never retried",
                partition, type);

            // The command may wait for the session (for example right after a takeover) before it is written. Once
            // this method gives up, it must never be written: the claim is taken right before the write, and
            // withdrawn here when the send failed before that, so the reply can say for sure that it was not sent.
            var claim = new SendClaim();
            var result = await SendCommandAsync(sessionId, new ActivateFap { Partition = partition, Type = type }, FapNoAnswerMessage,
                claim.TryClaimForSend);

            // A failure that is not a panel rejection, a NACK or a refusal before sending leaves it open whether the
            // panel raised the alarm, unless the command was never written.
            if (!result.Success && result.PanelResponseCode is null && !result.NotSent)
                result = claim.TryWithdraw()
                    ? result with { NotSent = true, TimedOut = false, UserMessage = FapNotSentMessage }
                    : result with { UserMessage = FapNoAnswerMessage };

            _logger.LogWarning("FAP command result: {Outcome}", result.Accepted ? "accepted by the panel" : result.DisplayMessage);
            return result;
        }

        /// <summary>
        /// Longest wait for the panel's answer to one command sent by this service. The TLink receivers
        /// have no timeout of their own, so without this bound an unanswered command would wait forever.
        /// </summary>
        internal static readonly TimeSpan PanelAnswerTimeout = TimeSpan.FromSeconds(8);

        private static readonly string PanelAnswerTimeoutMessage =
            $"No confirmation from the panel within {PanelAnswerTimeout.TotalSeconds:0} s; check the partition state";

        private async Task<PanelCommandResult> SendCommandAsync(string sessionId, IMessageData message, string? noAnswerMessage = null,
            Func<bool>? mayStillSend = null)
        {
            using var timeout = new CancellationTokenSource(PanelAnswerTimeout);
            try
            {
                // WaitAsync keeps the bound even if a lower layer does not observe the token.
                SessionResponse response = await _mediator.Send(new SessionCommand
                {
                    SessionID = sessionId,
                    MessageData = message,
                    MayStillSend = mayStillSend
                }, timeout.Token).WaitAsync(timeout.Token);

                if (response.Success)
                    return response.PanelNack is null
                        ? PanelCommandResult.Ok()
                        : PanelCommandResult.Ok() with
                        {
                            PanelNack = response.PanelNack,
                            PanelNackCode = response.PanelNackCode,
                            UserMessage = response.PanelNackCode is int nack ? PanelRejectionText.ForNackCode(nack) : null
                        };

                // SessionMediator turns the cancellation into a failed response, so check our own token.
                if (timeout.IsCancellationRequested && !response.PanelResponseCode.HasValue)
                    return PanelAnswerTimedOut(message, noAnswerMessage);

                _logger.LogWarning("Command failed: [{Code}] {Error}", response.ErrorCode, response.ErrorMessage);

                if (response.PanelResponseCode is int code)
                    return PanelCommandResult.Rejected(code, response.ErrorMessage ?? "Unknown error") with
                    {
                        UserMessage = PanelRejectionText.ForResponseCode(code, armCommand: message is PartitionArm)
                    };

                var error = response.ErrorCode.HasValue
                    ? PanelCommandResult.Error(response.ErrorCode.Value, response.ErrorMessage ?? response.ErrorCode.Value.ToString())
                    : PanelCommandResult.Error(response.ErrorMessage ?? "Unknown error");
                return response.NotSent ? error with { NotSent = true } : error;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return PanelAnswerTimedOut(message, noAnswerMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending command");
                return PanelCommandResult.Error(ex.Message);
            }
        }

        /// <summary>
        /// Decides once whether a command is written: <see cref="TryClaimForSend"/> right before the write, or
        /// <see cref="TryWithdraw"/> by the caller that gave up; whichever comes first wins.
        /// </summary>
        private sealed class SendClaim
        {
            private const int Open = 0, Claimed = 1, Withdrawn = 2;
            private int _state;

            public bool TryClaimForSend() => Interlocked.CompareExchange(ref _state, Claimed, Open) == Open;

            /// <summary>True when the command was not written and now never will be.</summary>
            public bool TryWithdraw() =>
                Interlocked.CompareExchange(ref _state, Withdrawn, Open) is Open or Withdrawn;
        }

        private PanelCommandResult PanelAnswerTimedOut(IMessageData message, string? noAnswerMessage)
        {
            // The panel may still act on the command; a late answer is dropped by the session.
            _logger.LogWarning("No answer from the panel to {Command} within {Seconds} s",
                message.GetType().Name, PanelAnswerTimeout.TotalSeconds);
            return PanelCommandResult.Error(noAnswerMessage ?? PanelAnswerTimeoutMessage) with { TimedOut = true };
        }
    }
}
