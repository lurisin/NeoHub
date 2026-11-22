using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DSC.TLink.ITv2.MediatR;
using NeoHub.Api.WebSocket.Models;
using NeoHub.Services;

namespace NeoHub.Api.WebSocket
{
    /// <summary>
    /// Manages WebSocket connections for Home Assistant integration.
    /// Pushes partition and zone updates in real-time.
    /// </summary>
    public class PanelWebSocketHandler
    {
        private readonly IPanelStateService _panelState;
        private readonly IPanelCommandService _commandService;
        private readonly IITv2SessionManager _sessionManager;
        private readonly ISessionMonitor _sessionMonitor;
        private readonly ILogger<PanelWebSocketHandler> _logger;
        private readonly IFapCommandSettings? _fapSettings;
        private readonly PartitionConditionsService? _conditions;
        private readonly ConcurrentDictionary<System.Net.WebSockets.WebSocket, byte> _connectedClients = new();

        /// <summary>Without FAP settings: activate_fap is always refused and "fap" never advertised.</summary>
        public PanelWebSocketHandler(
            IPanelStateService panelState,
            IPanelCommandService commandService,
            IITv2SessionManager sessionManager,
            ISessionMonitor sessionMonitor,
            ILogger<PanelWebSocketHandler> logger)
            : this(panelState, commandService, sessionManager, sessionMonitor, logger, fapSettings: null)
        {
        }

        /// <summary>Without the conditions service: no keypad_alarm messages.</summary>
        public PanelWebSocketHandler(
            IPanelStateService panelState,
            IPanelCommandService commandService,
            IITv2SessionManager sessionManager,
            ISessionMonitor sessionMonitor,
            ILogger<PanelWebSocketHandler> logger,
            IFapCommandSettings? fapSettings)
            : this(panelState, commandService, sessionManager, sessionMonitor, logger, fapSettings, conditions: null)
        {
        }

        public PanelWebSocketHandler(
            IPanelStateService panelState,
            IPanelCommandService commandService,
            IITv2SessionManager sessionManager,
            ISessionMonitor sessionMonitor,
            ILogger<PanelWebSocketHandler> logger,
            IFapCommandSettings? fapSettings,
            PartitionConditionsService? conditions)
        {
            _panelState = panelState;
            _commandService = commandService;
            _sessionManager = sessionManager;
            _sessionMonitor = sessionMonitor;
            _logger = logger;
            _fapSettings = fapSettings;
            _conditions = conditions;

            _logger.LogInformation("PanelWebSocketHandler initialized. Subscribed to state change events.");

            _sessionMonitor.SessionsChanged += OnSessionsChanged;
            _panelState.PartitionStateChanged += OnPartitionChanged;
            _panelState.ZoneStateChanged += OnZoneChanged;
            _panelState.ConfigurationComplete += OnConfigurationComplete;
            if (_fapSettings is not null)
                _fapSettings.Changed += OnFapSettingChanged;
            if (_conditions is not null)
                _conditions.KeypadAlarm += OnKeypadAlarm;
        }

        public async Task HandleConnectionAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                _logger.LogWarning("Rejected non-WebSocket request to /api/ws from {RemoteIp}", 
                    context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var clientId = Guid.NewGuid().ToString("N")[..8];
            _connectedClients.TryAdd(webSocket, 0);
            
            _logger.LogInformation("WebSocket client {ClientId} connected from {RemoteIp}. Total clients: {Count}",
                clientId, context.Connection.RemoteIpAddress, _connectedClients.Count);

            try
            {
                await ReceiveMessagesAsync(webSocket, clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSocket client {ClientId} error", clientId);
            }
            finally
            {
                _connectedClients.TryRemove(webSocket, out _);
                _logger.LogInformation("WebSocket client {ClientId} disconnected. Total clients: {Count}",
                    clientId, _connectedClients.Count);
                
                if (webSocket.State == WebSocketState.Open)
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }

        private async Task ReceiveMessagesAsync(System.Net.WebSockets.WebSocket webSocket, string clientId)
        {
            var buffer = new byte[4096];
            var segment = new ArraySegment<byte>(buffer);

            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(segment, CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogDebug("Client {ClientId} sent close frame", clientId);
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (_logger.IsEnabled(LogLevel.Trace))
                            _logger.LogTrace("Client {ClientId} ← {Json}", clientId, RedactAccessCodes(json));
                        await ProcessMessageAsync(webSocket, json, clientId);
                    }
                }
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                // Client disconnected abruptly (network drop, browser close, etc.)
                // This is normal behavior, not an error
                _logger.LogDebug("Client {ClientId} disconnected without close handshake", clientId);
            }
        }

        private async Task ProcessMessageAsync(System.Net.WebSockets.WebSocket webSocket, string json, string clientId)
        {
            try
            {
                var message = JsonSerializer.Deserialize<WebSocketMessage>(json, _jsonOptions);
                
                if (message == null)
                {
                    _logger.LogWarning("Client {ClientId} sent null message", clientId);
                    await SendErrorAsync(webSocket, "Invalid message", clientId);
                    return;
                }

                _logger.LogDebug("Client {ClientId} request: {MessageType}", clientId, message.GetType().Name);

                if (message is ICorrelatedCommandMessage { RequestId.Length: > ICorrelatedCommandMessage.MaxRequestIdLength })
                {
                    await SendErrorAsync(webSocket,
                        $"Invalid request_id: at most {ICorrelatedCommandMessage.MaxRequestIdLength} characters", clientId);
                    return;
                }

                switch (message)
                {
                    case GetFullStateMessage:
                        await SendFullStateAsync(webSocket, clientId);
                        break;

                    case ArmAwayMessage armAway:
                        await HandleArmCommandAsync(webSocket, armAway, DSC.TLink.ITv2.Enumerations.ArmingMode.AwayArm, clientId);
                        break;

                    case ArmHomeMessage armHome:
                        await HandleArmCommandAsync(webSocket, armHome, DSC.TLink.ITv2.Enumerations.ArmingMode.StayArm, clientId);
                        break;

                    case ArmNightMessage armNight:
                        await HandleArmCommandAsync(webSocket, armNight, DSC.TLink.ITv2.Enumerations.ArmingMode.NightArm, clientId);
                        break;

                    case ArmHomeInstantMessage armHomeInstant:
                        await HandleArmCommandAsync(webSocket, armHomeInstant, DSC.TLink.ITv2.Enumerations.ArmingMode.StayArmWithNoEntryDelay, clientId);
                        break;

                    case ArmAwayInstantMessage armAwayInstant:
                        await HandleArmCommandAsync(webSocket, armAwayInstant, DSC.TLink.ITv2.Enumerations.ArmingMode.AwayArmWithNoEntryDelay, clientId);
                        break;

                    case DisarmMessage disarm:
                        await HandleDisarmCommandAsync(webSocket, disarm, clientId);
                        break;

                    case BypassZoneMessage bypassZone:
                        await HandleBypassZoneCommandAsync(webSocket, bypassZone, clientId);
                        break;

                    case ActivateFapMessage activateFap:
                        await HandleActivateFapAsync(webSocket, activateFap, clientId);
                        break;

                    default:
                        _logger.LogWarning("Client {ClientId} sent unknown message type: {MessageType}", clientId, message.GetType().Name);
                        await SendErrorAsync(webSocket, $"Unknown message type: {message.GetType().Name}", clientId);
                        break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Client {ClientId} sent invalid JSON: {Json}", clientId, RedactAccessCodes(json));
                await SendErrorAsync(webSocket, $"Invalid JSON: {ex.Message}", clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message from client {ClientId}: {Json}", clientId, RedactAccessCodes(json));
                await SendErrorAsync(webSocket, $"Processing error: {ex.Message}", clientId);
            }
        }

        /// <summary>
        /// The value of a "code" property in raw client JSON, also when the JSON is invalid or cut off:
        /// a string (with escapes, possibly unterminated), a flat array or object, or a bare value such as a
        /// number, each up to the next ',', '}' or ']'.
        /// </summary>
        private static readonly Regex _accessCodeValue = new(
            @"(""code""\s*:\s*)(""(?:[^""\\]|\\.)*""?[^,}\]]*|\[[^\]]*\]?[^,}\]]*|\{[^}]*\}?[^,}\]]*|[^,}\]]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Raw client JSON for log lines, with every access code value replaced by "***" (null is kept).</summary>
        internal static string RedactAccessCodes(string json) =>
            _accessCodeValue.Replace(json, m =>
                m.Groups[2].Value.Trim() == "null" ? m.Value : m.Groups[1].Value + "\"***\"");

        private async Task SendFullStateAsync(System.Net.WebSockets.WebSocket webSocket, string clientId)
        {
            var sessions = _sessionManager.GetActiveSessions()
                .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId))
                .Select(sessionId =>
                {
                    var partitions = _panelState.GetPartitions(sessionId);
                    var zones = _panelState.GetZones(sessionId);
                    var lastKeypadAlarm = _panelState.GetSession(sessionId)?.LastKeypadAlarm;

                    return new SessionDto
                    {
                        SessionId = sessionId,
                        Name = sessionId,
                        Partitions = partitions
                            .Select(kvp => new PartitionDto
                            {
                                PartitionNumber = kvp.Key,
                                Name = kvp.Value.DisplayName,
                                Status = kvp.Value.EffectiveStatus,
                                Ready = ReadyForClients(kvp.Value),
                                Troubles = kvp.Value.TroublesPresent,
                                TroubleTypes = kvp.Value.TroubleTypes
                            })
                            .ToList(),
                        Zones = zones
                            .Select(kvp => new ZoneDto
                            {
                                ZoneNumber = kvp.Key,
                                Name = kvp.Value.DisplayName,
                                DeviceClass = DetermineDeviceClass(kvp.Value),
                                Open = kvp.Value.IsOpen,
                                Bypassed = kvp.Value.IsBypassed,
                                Partitions = kvp.Value.Partitions
                            })
                            .ToList(),
                        LastKeypadAlarm = lastKeypadAlarm is null ? null : new KeypadAlarmDto
                        {
                            Partition = lastKeypadAlarm.Partition,
                            AlarmType = lastKeypadAlarm.AlarmType,
                            Code = lastKeypadAlarm.Code,
                            Time = FormatTime(lastKeypadAlarm.Time)
                        }
                    };
                })
                .ToList();

            _logger.LogDebug(
                "Client {ClientId} → full_state: {SessionCount} sessions, {PartitionCount} partitions, {ZoneCount} zones",
                clientId,
                sessions.Count,
                sessions.Sum(s => s.Partitions.Count),
                sessions.Sum(s => s.Zones.Count));

            var message = new FullStateMessage { Sessions = sessions, Capabilities = CurrentCapabilities() };
            await SendMessageAsync(webSocket, message, clientId);
        }

        private async Task HandleArmCommandAsync(
            System.Net.WebSockets.WebSocket webSocket, 
            ArmCommandMessage command, 
            DSC.TLink.ITv2.Enumerations.ArmingMode mode, 
            string clientId)
        {
            _logger.LogInformation("Client {ClientId} → {Command}: Session={SessionId}, Partition={Partition}",
                clientId, command.GetType().Name, command.SessionId, command.PartitionNumber);

            var result = await RunCommandAsync(command, clientId,
                () => _commandService.ArmAsync(command.SessionId, command.PartitionNumber, mode, command.Code));

            await SendCommandOutcomeAsync(webSocket, command, result, "Command failed", clientId);
        }

        private async Task HandleDisarmCommandAsync(
            System.Net.WebSockets.WebSocket webSocket, 
            DisarmMessage command, 
            string clientId)
        {
            _logger.LogInformation("Client {ClientId} → {Command}: Session={SessionId}, Partition={Partition}",
                clientId, command.GetType().Name, command.SessionId, command.PartitionNumber);

            var result = await RunCommandAsync(command, clientId,
                () => _commandService.DisarmAsync(command.SessionId, command.PartitionNumber, command.Code));

            await SendCommandOutcomeAsync(webSocket, command, result, "Command failed", clientId);
        }

        private async Task HandleBypassZoneCommandAsync(
            System.Net.WebSockets.WebSocket webSocket,
            BypassZoneMessage command,
            string clientId)
        {
            _logger.LogInformation(
                "Client {ClientId} → bypass_zone: Session={SessionId}, Partition={Partition}, Zone={Zone}, Bypass={Bypass}",
                clientId, command.SessionId, command.PartitionNumber, command.ZoneNumber, command.Bypass);

            var result = await RunCommandAsync(command, clientId,
                () => _commandService.BypassZoneAsync(
                    command.SessionId, command.PartitionNumber, command.ZoneNumber, command.Bypass, command.Code));

            await SendCommandOutcomeAsync(webSocket, command, result, "Bypass command failed", clientId);
        }

        private static readonly IReadOnlyDictionary<string, DSC.TLink.ITv2.Enumerations.FapType> _fapTypes =
            new Dictionary<string, DSC.TLink.ITv2.Enumerations.FapType>(StringComparer.Ordinal)
            {
                ["fire"] = DSC.TLink.ITv2.Enumerations.FapType.Fire,
                ["auxiliary"] = DSC.TLink.ITv2.Enumerations.FapType.Auxiliary,
                ["panic"] = DSC.TLink.ITv2.Enumerations.FapType.Panic,
            };

        /// <summary>
        /// EXPERIMENTAL activate_fap: raises a real alarm reported to the monitoring station. Checked in this order,
        /// before any panel traffic: request_id (without one: an "error" message), the AllowFapCommands setting, then
        /// session_id, partition and fap_type (each answered with a failed command_result). Sent once through
        /// PanelCommandService; every request and its result are logged at Warning.
        /// </summary>
        private async Task HandleActivateFapAsync(System.Net.WebSockets.WebSocket webSocket, ActivateFapMessage command, string clientId)
        {
            if (command.RequestId is null)
            {
                _logger.LogWarning("Client {ClientId} → activate_fap refused, nothing sent: no request_id", clientId);
                await SendErrorAsync(webSocket, "activate_fap requires request_id", clientId);
                return;
            }

            var rejection = ValidateActivateFap(command, out var sessionId, out var partition, out var fapType);
            if (rejection is not null)
            {
                _logger.LogWarning("Client {ClientId} → activate_fap {RequestId} refused, nothing sent: {Reason}",
                    clientId, command.RequestId, rejection);
                await SendCommandOutcomeAsync(webSocket, command, PanelCommandResult.Error(rejection) with { NotSent = true },
                    "FAP command refused", clientId);
                return;
            }

            _logger.LogWarning(
                "Client {ClientId} → activate_fap {RequestId}: Session={SessionId}, Partition={Partition}, Type={FapType} (experimental, raises a real alarm)",
                clientId, command.RequestId, sessionId, partition, fapType);

            var result = await RunCommandAsync(command, clientId,
                () => _commandService.ActivateFapAsync(sessionId, partition, fapType));

            _logger.LogWarning("Client {ClientId} → activate_fap {RequestId} result: {Outcome}",
                clientId, command.RequestId, result.Accepted ? "accepted by the panel" : result.DisplayMessage);

            await SendCommandOutcomeAsync(webSocket, command, result, "FAP command failed", clientId);
        }

        /// <summary>The reason to refuse an activate_fap request, or null when it may be sent.</summary>
        private string? ValidateActivateFap(ActivateFapMessage command, out string sessionId, out byte partition,
            out DSC.TLink.ITv2.Enumerations.FapType fapType)
        {
            sessionId = string.Empty;
            partition = 0;
            fapType = default;

            if (_fapSettings?.AllowFapCommands != true)
                return DSC.TLink.ITv2.Messages.ActivateFap.DisabledMessage;

            if (command.SessionIdValue.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(command.SessionIdValue.GetString()))
                return "activate_fap requires session_id";
            sessionId = command.SessionIdValue.GetString()!;
            if (!_sessionManager.GetActiveSessions().Contains(sessionId))
                return $"Session {sessionId} not found";

            if (command.PartitionValue.ValueKind != JsonValueKind.Number
                || !command.PartitionValue.TryGetInt32(out var number)
                || number < 1)
                return "activate_fap requires partition: a partition number of 1 or more";
            if (number > byte.MaxValue || _panelState.GetPartition(sessionId, (byte)number) is null)
                return $"Partition {number} is not a known partition of session {sessionId}";
            partition = (byte)number;

            if (command.FapTypeValue.ValueKind != JsonValueKind.String
                || !_fapTypes.TryGetValue(command.FapTypeValue.GetString()!, out fapType))
                return "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"";

            return null;
        }

        /// <summary>
        /// Runs a panel command. Without a request_id an exception propagates to ProcessMessageAsync as
        /// before (generic "error" message); with one it becomes a failed result, so the client still gets
        /// its single command_result.
        /// </summary>
        private async Task<PanelCommandResult> RunCommandAsync(
            ICorrelatedCommandMessage command,
            string clientId,
            Func<Task<PanelCommandResult>> run)
        {
            if (command.RequestId is null)
                return await run();

            try
            {
                return await run();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing {MessageType} from client {ClientId}",
                    command.GetType().Name, clientId);
                return PanelCommandResult.Error($"Processing error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reports a command's outcome. Without a request_id: an "error" message on failure and nothing on
        /// success, as before. With one: exactly one command_result to this client.
        /// </summary>
        private async Task SendCommandOutcomeAsync(
            System.Net.WebSockets.WebSocket webSocket,
            ICorrelatedCommandMessage command,
            PanelCommandResult result,
            string defaultErrorMessage,
            string clientId)
        {
            if (command.RequestId is null)
            {
                if (!result.Success)
                    await SendErrorAsync(webSocket, result.ErrorMessage ?? defaultErrorMessage, clientId);
                return;
            }

            // A NACK from the panel interface (Command_Error) counts as success for the "error" path above,
            // as it always did, but the command was not carried out, so a command_result reports a failure,
            // with the NACK code as "code" and "code_kind": "nack".
            var message = result switch
            {
                { Success: true, PanelNack: null } =>
                    new CommandResultMessage { RequestId = command.RequestId, Success = true },
                { Success: true } =>
                    new CommandResultMessage
                    {
                        RequestId = command.RequestId,
                        Success = false,
                        Code = result.PanelNackCode,
                        CodeKind = result.PanelNackCode is null ? null : CommandResultMessage.NackCodeKind,
                        Message = result.UserMessage ?? $"Command refused by the panel interface (NACK: {result.PanelNack})"
                    },
                _ =>
                    new CommandResultMessage
                    {
                        RequestId = command.RequestId,
                        Success = false,
                        Code = result.PanelResponseCode,
                        CodeKind = result.PanelResponseCode is null ? null : CommandResultMessage.ResponseCodeKind,
                        Message = result.DisplayMessage ?? defaultErrorMessage
                    }
            };

            if (message.Success)
                _logger.LogDebug("Client {ClientId} → command_result {RequestId}: success", clientId, command.RequestId);
            else
                _logger.LogWarning("Client {ClientId} → command_result {RequestId}: failed, code {Code} ({CodeKind}): {Message}",
                    clientId, command.RequestId, message.Code, message.CodeKind, message.Message);

            try
            {
                await SendMessageAsync(webSocket, message, clientId);
            }
            catch (Exception ex)
            {
                // Typically the client went away while the command ran. Caught here so that it does not reach
                // ProcessMessageAsync, which would answer with an "error" message instead of the command_result.
                _logger.LogWarning("Could not send command_result {RequestId} to client {ClientId}: {Error}",
                    command.RequestId, clientId, ex.Message);
            }
        }

        /// <summary>
        /// Sent in full_state so clients know which optional protocol features they can use, in this order. New
        /// capabilities are appended here; "fap" always comes last (<see cref="CurrentCapabilities"/>).
        /// </summary>
        private static readonly string[] _capabilities = { "command_result", "partition_ready", "partition_troubles", "trouble_types", "keypad_alarms" };

        /// <summary>Advertised, last, only while AllowFapCommands is on.</summary>
        internal const string FapCapability = "fap";

        private static readonly string[] _capabilitiesWithFap = _capabilities.Append(FapCapability).ToArray();

        private IReadOnlyList<string> CurrentCapabilities() =>
            _fapSettings?.AllowFapCommands == true ? _capabilitiesWithFap : _capabilities;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        private async Task SendMessageAsync(System.Net.WebSockets.WebSocket webSocket, WebSocketMessage message, string clientId)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                _logger.LogTrace("Skipping send to client {ClientId} (state: {State})", clientId, webSocket.State);
                return;
            }

            // Serialize as the base type so the [JsonPolymorphic] discriminator is emitted
            var json = JsonSerializer.Serialize<WebSocketMessage>(message, _jsonOptions);

            _logger.LogTrace("Client {ClientId} → {MessageType}: {Json}", clientId, message.GetType().Name, json);

            var maxByteCount = Encoding.UTF8.GetMaxByteCount(json.Length);
            var rentedBuffer = ArrayPool<byte>.Shared.Rent(maxByteCount);
            try
            {
                var byteCount = Encoding.UTF8.GetBytes(json, 0, json.Length, rentedBuffer, 0);
                await webSocket.SendAsync(new ArraySegment<byte>(rentedBuffer, 0, byteCount), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

        private async Task SendErrorAsync(System.Net.WebSockets.WebSocket webSocket, string errorMessage, string clientId)
        {
            _logger.LogWarning("Client {ClientId} → error: {Message}", clientId, errorMessage);
            await SendMessageAsync(webSocket, new ErrorMessage { Message = errorMessage }, clientId);
        }

        #region Event Handlers (broadcast to all clients)

        private void OnSessionsChanged()
        {
            _logger.LogDebug("Session list changed, broadcasting full_state to {Count} clients", 
                _connectedClients.Keys.Count(c => c.State == WebSocketState.Open));
            _ = BroadcastFullStateAsync();
        }

        private void OnFapSettingChanged()
        {
            _logger.LogInformation("FAP setting changed, broadcasting full_state to {Count} clients",
                _connectedClients.Keys.Count(c => c.State == WebSocketState.Open));
            _ = BroadcastFullStateAsync();
        }

        private void OnConfigurationComplete(object? sender, ConfigurationCompleteEventArgs e)
        {
            _logger.LogInformation("Configuration complete for session {SessionId}, broadcasting full_state to {Count} clients",
                e.SessionId, _connectedClients.Keys.Count(c => c.State == WebSocketState.Open));
            _ = BroadcastFullStateAsync();
        }

        private void OnPartitionChanged(object? sender, PartitionStateChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.SessionId))
            {
                _logger.LogWarning("Ignoring partition update with empty sessionId");
                return;
            }

            _logger.LogDebug("Broadcasting partition_update: Session={SessionId}, Partition={Partition}, Status={Status}, Ready={Ready}, Troubles={Troubles}",
                e.SessionId, e.Partition.PartitionNumber, e.Partition.EffectiveStatus, ReadyForClients(e.Partition), e.Partition.TroublesPresent);

            // Raised for every partition update, including a ready-status, trouble or trouble type change with no status
            // change.
            var message = new PartitionUpdateMessage
            {
                SessionId = e.SessionId,
                PartitionNumber = e.Partition.PartitionNumber,
                Status = e.Partition.EffectiveStatus,
                Ready = ReadyForClients(e.Partition),
                Troubles = e.Partition.TroublesPresent,
                TroubleTypes = e.Partition.TroubleTypes
            };

            _ = BroadcastMessageAsync(message);
        }

        private void OnKeypadAlarm(object? sender, KeypadAlarmEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.SessionId))
            {
                _logger.LogWarning("Ignoring keypad alarm with empty sessionId");
                return;
            }

            _logger.LogDebug("Broadcasting keypad_alarm: Session={SessionId}, Partition={Partition}, AlarmType={AlarmType}, Code={Code}",
                e.SessionId, e.Alarm.Partition, e.Alarm.AlarmType, e.Alarm.Code);

            _ = BroadcastMessageAsync(new KeypadAlarmMessage
            {
                SessionId = e.SessionId,
                Partition = e.Alarm.Partition,
                AlarmType = e.Alarm.AlarmType,
                Code = e.Alarm.Code,
                Time = FormatTime(e.Alarm.Time)
            });
        }

        private void OnZoneChanged(object? sender, ZoneStateChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.SessionId))
            {
                _logger.LogWarning("Ignoring zone update with empty sessionId");
                return;
            }

            _logger.LogDebug("Broadcasting zone_update: Session={SessionId}, Zone={Zone}, Open={Open}, Bypassed={Bypassed}",
                e.SessionId, e.Zone.ZoneNumber, e.Zone.IsOpen, e.Zone.IsBypassed);

            var message = new ZoneUpdateMessage
            {
                SessionId = e.SessionId,
                ZoneNumber = e.Zone.ZoneNumber,
                Open = e.Zone.IsOpen,
                Bypassed = e.Zone.IsBypassed
            };

            _ = BroadcastMessageAsync(message);
        }

        private async Task BroadcastFullStateAsync()
        {
            var openClients = _connectedClients.Keys.Where(c => c.State == WebSocketState.Open).ToList();
            _logger.LogTrace("Broadcasting full_state to {Count} clients", openClients.Count);

            foreach (var client in openClients)
            {
                try
                {
                    await SendFullStateAsync(client, "broadcast");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error broadcasting full state to client");
                }
            }
        }

        private async Task BroadcastMessageAsync(WebSocketMessage message)
        {
            var openClients = _connectedClients.Keys.Where(c => c.State == WebSocketState.Open).ToList();
            _logger.LogTrace("Broadcasting {MessageType} to {Count} clients", message.GetType().Name, openClients.Count);

            foreach (var client in openClients)
            {
                try
                {
                    await SendMessageAsync(client, message, "broadcast");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error broadcasting {MessageType} to client", message.GetType().Name);
                }
            }
        }

        #endregion

        #region Helpers

        /// <summary>A UTC time for clients: ISO 8601 with milliseconds and "Z".</summary>
        internal static string FormatTime(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        /// <summary>
        /// The "ready" flag sent to clients: the panel's ready flag while the partition is disarmed, otherwise false.
        /// PartitionState.IsReady only changes on ready-status notifications, which the panel sends while disarmed,
        /// so after arming it would keep its last value (usually true); the status pull already sets it to false while
        /// armed. Based on EffectiveStatus, so the exit delay counts as not ready too.
        /// </summary>
        private static bool ReadyForClients(Services.Models.PartitionState partition) =>
            partition.IsReady && partition.EffectiveStatus == Services.Models.PartitionStatus.Disarmed;

        private static string DetermineDeviceClass(Services.Models.ZoneState zone)
        {
            // TODO: Zone type configuration
            return "door";
        }

        #endregion
    }
}