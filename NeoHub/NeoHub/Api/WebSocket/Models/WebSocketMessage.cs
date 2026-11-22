using System.Text.Json;
using System.Text.Json.Serialization;
using NeoHub.Services.Models;

namespace NeoHub.Api.WebSocket.Models
{
    // Base message for polymorphic deserialization
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(GetFullStateMessage), "get_full_state")]
    [JsonDerivedType(typeof(ArmAwayMessage), "arm_away")]
    [JsonDerivedType(typeof(ArmHomeMessage), "arm_home")]
    [JsonDerivedType(typeof(ArmNightMessage), "arm_night")]
    [JsonDerivedType(typeof(ArmHomeInstantMessage), "arm_home_instant")]
    [JsonDerivedType(typeof(ArmAwayInstantMessage), "arm_away_instant")]
    [JsonDerivedType(typeof(DisarmMessage), "disarm")]
    [JsonDerivedType(typeof(BypassZoneMessage), "bypass_zone")]
    [JsonDerivedType(typeof(ActivateFapMessage), "activate_fap")]
    [JsonDerivedType(typeof(FullStateMessage), "full_state")]
    [JsonDerivedType(typeof(PartitionUpdateMessage), "partition_update")]
    [JsonDerivedType(typeof(ZoneUpdateMessage), "zone_update")]
    [JsonDerivedType(typeof(ErrorMessage), "error")]
    [JsonDerivedType(typeof(CommandResultMessage), "command_result")]
    [JsonDerivedType(typeof(KeypadAlarmMessage), "keypad_alarm")]
    public abstract record WebSocketMessage;

    #region Client → Server

    public record GetFullStateMessage : WebSocketMessage;

    /// <summary>
    /// A client command that accepts an optional <c>request_id</c> (a client-chosen string of at most
    /// <see cref="MaxRequestIdLength"/> characters). When it is set, the server answers that client with
    /// exactly one <see cref="CommandResultMessage"/> carrying the same id, instead of an <c>error</c> on failure.
    /// </summary>
    public interface ICorrelatedCommandMessage
    {
        const int MaxRequestIdLength = 64;

        string? RequestId { get; }
    }

    public abstract record ArmCommandMessage : WebSocketMessage, ICorrelatedCommandMessage
    {
        public required string SessionId { get; init; }
        public required byte PartitionNumber { get; init; }
        public string? Code { get; init; }
        public string? RequestId { get; init; }
    }

    public record ArmAwayMessage : ArmCommandMessage;
    public record ArmHomeMessage : ArmCommandMessage;
    public record ArmNightMessage : ArmCommandMessage;
    public record DisarmMessage : ArmCommandMessage;

    /// <summary>Stay arm with no entry delay ("Stay Instant"): ArmingMode.StayArmWithNoEntryDelay.</summary>
    public record ArmHomeInstantMessage : ArmCommandMessage;

    /// <summary>Away arm with no entry delay ("Away Instant"): ArmingMode.AwayArmWithNoEntryDelay (untested on a real panel).</summary>
    public record ArmAwayInstantMessage : ArmCommandMessage;

    public record BypassZoneMessage : WebSocketMessage, ICorrelatedCommandMessage
    {
        public required string SessionId { get; init; }
        public required byte PartitionNumber { get; init; }
        public required byte ZoneNumber { get; init; }
        /// <summary>true = bypass, false = unbypass</summary>
        public required bool Bypass { get; init; }
        public string? Code { get; init; }
        public string? RequestId { get; init; }
    }

    /// <summary>
    /// EXPERIMENTAL: raise a Fire, Auxiliary or Panic alarm on a partition (ModuleControl_Activate_FAP), only while
    /// AllowFapCommands is on in the NeoHub settings. All fields are required:
    /// <c>{"type":"activate_fap","session_id":"..","partition":1,"fap_type":"fire"|"auxiliary"|"panic","request_id":".."}</c>.
    /// The FAP kind is "fap_type" because "type" is the message discriminator. The fields are kept as raw JSON and
    /// validated by the handler, so a missing or wrong value gets a command_result (or, without request_id, an
    /// "error"), never a JSON error and never a default.
    /// </summary>
    public record ActivateFapMessage : WebSocketMessage, ICorrelatedCommandMessage
    {
        [JsonPropertyName("session_id")]
        public JsonElement SessionIdValue { get; init; }

        [JsonPropertyName("partition")]
        public JsonElement PartitionValue { get; init; }

        [JsonPropertyName("fap_type")]
        public JsonElement FapTypeValue { get; init; }

        [JsonPropertyName("request_id")]
        public JsonElement RequestIdValue { get; init; }

        /// <summary>The request_id when it is a JSON string; otherwise null (treated as missing).</summary>
        [JsonIgnore]
        public string? RequestId => RequestIdValue.ValueKind == JsonValueKind.String ? RequestIdValue.GetString() : null;
    }

    #endregion

    #region Server → Client Messages

    public record FullStateMessage : WebSocketMessage
    {
        public required List<SessionDto> Sessions { get; init; }

        /// <summary>Optional protocol features this server supports, so clients can detect them.</summary>
        public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    }

    public record PartitionUpdateMessage : WebSocketMessage
    {
        public required string SessionId { get; init; }
        public required byte PartitionNumber { get; init; }
        public required PartitionStatus Status { get; init; }
        /// <summary>The partition is disarmed and the panel reports it ready to arm (PartitionState.IsReady).</summary>
        public bool Ready { get; init; }
        /// <summary>The panel reports one or more troubles for the partition (PartitionState.TroublesPresent).</summary>
        public bool Troubles { get; init; }
        /// <summary>The active trouble types while <see cref="Troubles"/> is set, otherwise empty (PartitionState.TroubleTypes).</summary>
        public IReadOnlyList<string> TroubleTypes { get; init; } = Array.Empty<string>();
    }

    public record ZoneUpdateMessage : WebSocketMessage
    {
        public required string SessionId { get; init; }
        public required byte ZoneNumber { get; init; }
        public required bool Open { get; init; }
        public required bool Bypassed { get; init; }
    }

    public record ErrorMessage : WebSocketMessage
    {
        public required string Message { get; init; }
    }

    /// <summary>
    /// Outcome of a command that carried a request_id, sent only to the client that sent it.
    /// <see cref="Code"/> is the panel's numeric code when the panel did not carry the command out, and
    /// <see cref="CodeKind"/> says which numbering it belongs to: "response" for a <c>Command_Response</c> code
    /// (a panel-level rejection) or "nack" for the NACK code of a <c>Command_Error</c> (the panel interface refused
    /// it). The two numberings overlap with different meanings. Both are null whenever there is no panel code
    /// (success, session not found, validation errors, no answer in time, ...).
    /// On success, <see cref="Code"/>, <see cref="CodeKind"/> and <see cref="Message"/> are null.
    /// </summary>
    public record CommandResultMessage : WebSocketMessage
    {
        public const string ResponseCodeKind = "response";
        public const string NackCodeKind = "nack";

        public required string RequestId { get; init; }
        public required bool Success { get; init; }
        public int? Code { get; init; }
        public string? CodeKind { get; init; }
        public string? Message { get; init; }
    }

    /// <summary>
    /// The panel reported a keypad alarm (Fire / Auxiliary / Panic keys; ModuleStatus_Miscellaneous_NonZone_Alarm_Status
    /// with state 01). Broadcast for any partition number, also one the client does not know (for example 0). The
    /// partition's status is not changed by it. The kind is "alarm_type" because "type" is the message discriminator.
    /// </summary>
    public record KeypadAlarmMessage : WebSocketMessage
    {
        public required string SessionId { get; init; }
        public required int Partition { get; init; }
        /// <summary>"fire" (alarm type 03) or "unknown" (any other value).</summary>
        public required string AlarmType { get; init; }
        /// <summary>The raw alarm type byte.</summary>
        public required int Code { get; init; }
        /// <summary>When NeoHub received it: UTC, ISO 8601 with milliseconds, NeoHub's clock.</summary>
        public required string Time { get; init; }
    }

    #endregion

    #region DTOs

    public record SessionDto
    {
        public required string SessionId { get; init; }
        public required string Name { get; init; }
        public required List<PartitionDto> Partitions { get; init; }
        public required List<ZoneDto> Zones { get; init; }
        /// <summary>The session's last keypad alarm, or null (SessionState.LastKeypadAlarm).</summary>
        public KeypadAlarmDto? LastKeypadAlarm { get; init; }
    }

    public record KeypadAlarmDto
    {
        public required int Partition { get; init; }
        public required string AlarmType { get; init; }
        public required int Code { get; init; }
        public required string Time { get; init; }
    }

    public record PartitionDto
    {
        public required byte PartitionNumber { get; init; }
        public required string Name { get; init; }
        public required PartitionStatus Status { get; init; }
        /// <summary>The partition is disarmed and the panel reports it ready to arm (PartitionState.IsReady).</summary>
        public bool Ready { get; init; }
        /// <summary>The panel reports one or more troubles for the partition (PartitionState.TroublesPresent).</summary>
        public bool Troubles { get; init; }
        /// <summary>The active trouble types while <see cref="Troubles"/> is set, otherwise empty (PartitionState.TroubleTypes).</summary>
        public IReadOnlyList<string> TroubleTypes { get; init; } = Array.Empty<string>();
    }

    public record ZoneDto
    {
        public required byte ZoneNumber { get; init; }
        public required string Name { get; init; }
        public required string DeviceClass { get; init; }
        public required bool Open { get; init; }
        public required bool Bypassed { get; init; }
        public required List<byte> Partitions { get; init; }
    }

    #endregion
}