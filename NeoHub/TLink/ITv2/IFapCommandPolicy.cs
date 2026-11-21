namespace DSC.TLink.ITv2;

/// <summary>
/// Decides whether <see cref="Messages.ActivateFap"/> (ModuleControl_Activate_FAP, 0x0908) may be sent: it raises a
/// real alarm that the panel reports to the monitoring station. <see cref="MediatR.SessionMediator"/> sends it only
/// while a registered policy allows it; with no policy registered it is never sent.
/// </summary>
public interface IFapCommandPolicy
{
    bool AllowFapCommands { get; }
}
