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
    [JsonDerivedType(typeof(FullStateMessage), "full_state")]
    [JsonDerivedType(typeof(PartitionUpdateMessage), "partition_update")]
    [JsonDerivedType(typeof(ZoneUpdateMessage), "zone_update")]
    [JsonDerivedType(typeof(ErrorMessage), "error")]
    [JsonDerivedType(typeof(CommandResultMessage), "command_result")]
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
    /// <see cref="Code"/> is the panel's numeric response code when the panel rejected the command,
    /// otherwise null. On success, <see cref="Code"/> and <see cref="Message"/> are null.
    /// </summary>
    public record CommandResultMessage : WebSocketMessage
    {
        public required string RequestId { get; init; }
        public required bool Success { get; init; }
        public int? Code { get; init; }
        public string? Message { get; init; }
    }

    #endregion

    #region DTOs

    public record SessionDto
    {
        public required string SessionId { get; init; }
        public required string Name { get; init; }
        public required List<PartitionDto> Partitions { get; init; }
        public required List<ZoneDto> Zones { get; init; }
    }

    public record PartitionDto
    {
        public required byte PartitionNumber { get; init; }
        public required string Name { get; init; }
        public required PartitionStatus Status { get; init; }
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