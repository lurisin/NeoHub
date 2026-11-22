using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NeoHub.Services;
using NeoHub.Services.Settings;
using NeoHub.Tests.Infrastructure;

namespace NeoHub.Tests;

/// <summary>
/// The Settings page edits what <see cref="ISettingsPersistenceService.GetSettings"/> returns (here through the real
/// Program wiring): an edit that is not saved must not show on a later load of the page, and must not be saved by a
/// later Save of the same section.
/// </summary>
public class SettingsPageTests
{
    private static ApplicationSettings Load(ProgramHost host) =>
        (ApplicationSettings)host.Services.GetRequiredService<ISettingsPersistenceService>().GetSettings(typeof(ApplicationSettings));

    [Fact]
    public void An_unsaved_FAP_switch_does_not_show_on_the_next_load_and_changes_nothing()
    {
        using var host = ProgramHost.Build("""{"Application":{"ListenPort":3072,"AllowFapCommands":true}}""");
        var fap = host.Services.GetRequiredService<IFapCommandSettings>();
        var live = host.Services.GetRequiredService<IOptionsMonitor<ApplicationSettings>>();
        Assert.True(fap.AllowFapCommands);

        // Visit 1 moves the switch off and leaves without Save.
        var visit1 = Load(host);
        Assert.True(visit1.AllowFapCommands);
        visit1.AllowFapCommands = false;

        // Visit 2 starts from the configuration again: it shows what is in effect.
        var visit2 = Load(host);
        Assert.NotSame(visit1, visit2);
        Assert.True(visit2.AllowFapCommands);
        Assert.True(fap.AllowFapCommands);
        Assert.NotSame(visit1, live.CurrentValue);
        Assert.True(live.CurrentValue.AllowFapCommands);
    }

    [Fact]
    public async Task An_unsaved_FAP_switch_is_not_saved_by_a_later_save_of_the_section()
    {
        using var host = ProgramHost.Build("""{"Application":{"ListenPort":3072,"AllowFapCommands":false}}""");
        var persistence = host.Services.GetRequiredService<ISettingsPersistenceService>();
        var fap = host.Services.GetRequiredService<IFapCommandSettings>();

        // Visit 1 moves the switch on and leaves without Save.
        Load(host).AllowFapCommands = true;

        // Visit 2 shows it off; only the port is changed and the section is saved.
        var visit2 = Load(host);
        Assert.False(visit2.AllowFapCommands);
        visit2.ListenPort = 3073;
        await persistence.SaveSettingsAsync(typeof(ApplicationSettings), visit2);

        Assert.False(fap.AllowFapCommands);
        var file = await File.ReadAllTextAsync(host.SettingsFile);
        Assert.Contains("\"AllowFapCommands\": false", file);
        Assert.Contains("\"ListenPort\": 3073", file);
        Assert.Equal(3073, Load(host).ListenPort);
    }

    [Fact]
    public async Task Saving_the_page_copy_applies_it()
    {
        // Positive control: what the page saves is applied, and the next load shows it.
        using var host = ProgramHost.Build("""{"Application":{"ListenPort":3072}}""");
        var persistence = host.Services.GetRequiredService<ISettingsPersistenceService>();
        var fap = host.Services.GetRequiredService<IFapCommandSettings>();
        Assert.False(fap.AllowFapCommands);

        var page = Load(host);
        page.AllowFapCommands = true;
        Assert.False(fap.AllowFapCommands);
        await persistence.SaveSettingsAsync(typeof(ApplicationSettings), page);
        Assert.True(fap.AllowFapCommands);
        Assert.True(Load(host).AllowFapCommands);

        page = Load(host);
        page.AllowFapCommands = false;
        await persistence.SaveSettingsAsync(typeof(ApplicationSettings), page);
        Assert.False(fap.AllowFapCommands);
        Assert.False(Load(host).AllowFapCommands);
    }
}
