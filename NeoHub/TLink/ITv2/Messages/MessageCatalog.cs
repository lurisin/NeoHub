using DSC.TLink.ITv2.Enumerations;
using System.Reflection;

namespace DSC.TLink.ITv2.Messages;

/// <summary>
/// Describes a registered ITv2 message type, for dev/testing tooling.
/// </summary>
public sealed record MessageTypeInfo(ITv2Command Command, Type MessageType, string DisplayName);

/// <summary>
/// Public catalog of all ITv2 message types registered with <see cref="MessageFactory"/>.
/// Intended for development and testing tooling only.
/// </summary>
public static class MessageCatalog
{
    private static readonly IReadOnlyList<MessageTypeInfo> _all;

    static MessageCatalog()
    {
        _all = MessageFactory.GetRegisteredTypes()
            .Select(pair => new MessageTypeInfo(
                pair.Command,
                pair.MessageType,
                pair.Command.ToString().Replace('_', ' ')))
            .OrderBy(m => m.DisplayName)
            .ToList();
    }

    public static IReadOnlyList<MessageTypeInfo> GetAll() => _all;

    /// <summary>
    /// Returns only the types that can be sent as outbound commands — i.e., subclasses of
    /// <see cref="CommandMessageBase"/>. These are the only types useful to send from a dev console.
    /// Commands in <see cref="NotForTools"/> are left out.
    /// </summary>
    public static IReadOnlyList<MessageTypeInfo> GetSendable() =>
        _all.Where(m => typeof(CommandMessageBase).IsAssignableFrom(m.MessageType) && !NotForTools.Contains(m.Command))
            .ToList();

    /// <summary>
    /// Commands that development tools must not offer: ModuleControl_Activate_FAP raises a real alarm reported to the
    /// monitoring station (see <see cref="ActivateFap"/>).
    /// </summary>
    public static IReadOnlySet<ITv2Command> NotForTools { get; } = new HashSet<ITv2Command> { ITv2Command.ModuleControl_Activate_FAP };

    /// <summary>
    /// Returns user-editable properties for a message type.
    /// Excludes <see cref="CommandMessageBase.CommandSequence"/>, which is managed by the session layer.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> GetEditableProperties(Type messageType) =>
        messageType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.Name != nameof(CommandMessageBase.CommandSequence))
            .ToList();
}
