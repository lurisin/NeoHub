namespace NeoHub.Services.Settings
{
    /// <summary>
    /// A warning the Settings page shows next to a setting, for options with consequences outside NeoHub.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SettingsWarningAttribute : Attribute
    {
        public SettingsWarningAttribute(string text) => Text = text;

        public string Text { get; }
    }
}
