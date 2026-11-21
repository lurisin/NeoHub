using System.ComponentModel.DataAnnotations;
using DSC.TLink.ITv2;

namespace NeoHub.Services.Settings
{
    /// <summary>
    /// Application-level settings for user preferences and panel control options.
    /// Binds to the "Application" section in appsettings.json / userSettings.json
    /// </summary>
    [Display(Name = "Application Settings", Description = "User preferences and panel control options")]
    public class ApplicationSettings
    {
        public const string SectionName = "Application";

        /// <summary>
        /// TCP port for panel connections (default: 3072)
        /// </summary>
        [Display(
            Name = "Server Port",
            Description = "TCP port for panel connections",
            GroupName = "Network",
            Order = 10)]
        [Range(1, 65535)]
        public int ListenPort { get; set; } = ConnectionSettings.DefaultListenPort;

        /// <summary>
        /// Lets WebSocket clients (Home Assistant) send ModuleControl_Activate_FAP: a Fire, Auxiliary or Panic alarm.
        /// Experimental and off by default; a settings file without it loads as off. Read from the saved settings by
        /// <see cref="FapCommandSettings"/>.
        /// </summary>
        [Display(
            Name = "Allow FAP commands (Fire / Auxiliary / Panic)",
            Description = "Experimental, off by default. Applies when saved.",
            GroupName = "Panel control",
            Order = 20)]
        [SettingsWarning("When this is on, Home Assistant (or any WebSocket client) can raise a Fire, Auxiliary or Panic alarm on the panel. "
            + "It is a REAL alarm: the panel sounds it and reports it to the monitoring station. "
            + "Turn it on only for a test agreed with the monitoring station, and off again afterwards.")]
        public bool AllowFapCommands { get; set; }
    }
}
