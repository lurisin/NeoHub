using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.Serialization;

namespace DSC.TLink.ITv2.Messages
{
    /// <summary>
    /// ModuleControl_Activate_FAP (0x0908): raises a Fire, Auxiliary or Panic alarm on a partition, as the F / A / P
    /// keys of a keypad do. The panel sounds it and reports it to the monitoring station.
    ///
    /// EXPERIMENTAL. Body <c>[command sequence][CompactInt partition][type]</c>, no access code, as in
    /// LawPaul/HA_DSC_Neo_ITv2 (GPL-3.0), dsc_itv2/messages.py, class ModuleControlActivateFAP, whose docstring says
    /// "Layout per dsc-itv2-protocol observations" without citing them; not verified on a panel. The panel's answer to
    /// it is not documented.
    ///
    /// <see cref="MediatR.SessionMediator"/> refuses to send it unless an <see cref="IFapCommandPolicy"/> allows it, and
    /// <see cref="MessageCatalog.GetSendable"/> does not list it. Sealed, so no other type can carry this command word.
    /// </summary>
    [ITv2Command(ITv2Command.ModuleControl_Activate_FAP)]
    public sealed record ActivateFap : CommandMessageBase
    {
        /// <summary>The answer to a FAP request while FAP commands are not allowed.</summary>
        public const string DisabledMessage = "FAP commands are disabled in the NeoHub settings";

        [CompactInteger]
        public required int Partition { get; init; }

        public required FapType Type { get; init; }
    }
}
