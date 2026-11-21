using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NeoHub.Services;
using NeoHub.Services.Settings;
using NeoHub.Tests.Infrastructure;

namespace NeoHub.Tests;

/// <summary>The AllowFapCommands setting: off by default, only the saved value counts, and a change is announced.</summary>
public class FapSettingTests
{
    private static IConfigurationRoot Config(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    private static IConfigurationRoot Memory(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Application:ListenPort"] = "3072",
            [FapCommandSettings.ConfigurationKey] = value,
        }).Build();

    [Theory]
    [InlineData("""{}""", false)]
    [InlineData("""{"Application":{"ListenPort":3072}}""", false)]
    [InlineData("""{"Application":{"ListenPort":3072,"AllowFapCommands":false}}""", false)]
    [InlineData("""{"Application":{"ListenPort":3072,"AllowFapCommands":true}}""", true)]
    [InlineData("""{"Application":{"AllowFapCommands":"yes"}}""", false)]
    public void The_saved_settings_file_decides_and_missing_or_invalid_means_off(string json, bool expected)
    {
        using var settings = new FapCommandSettings(Config(json), NullLogger<FapCommandSettings>.Instance);
        Assert.Equal(expected, settings.AllowFapCommands);
    }

    [Fact]
    public void The_options_default_is_off()
    {
        Assert.False(new ApplicationSettings().AllowFapCommands);
        var services = new ServiceCollection();
        services.Configure<ApplicationSettings>(Config("""{"Application":{"ListenPort":3072}}""").GetSection(ApplicationSettings.SectionName));
        Assert.False(services.BuildServiceProvider().GetRequiredService<IOptions<ApplicationSettings>>().Value.AllowFapCommands);
    }

    [Fact]
    public void A_reload_with_a_new_value_raises_Changed_once_and_the_same_value_does_not()
    {
        var config = Memory(null);
        using var settings = new FapCommandSettings(config, NullLogger<FapCommandSettings>.Instance);
        var changes = new List<bool>();
        settings.Changed += () => changes.Add(settings.AllowFapCommands);

        config[FapCommandSettings.ConfigurationKey] = "true";
        config.Reload();
        config.Reload();
        config[FapCommandSettings.ConfigurationKey] = "false";
        config.Reload();

        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public void Moving_the_switch_without_saving_changes_nothing()
    {
        // The Settings page binds to the live options object and changes it before Save.
        var config = Memory("false");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<ApplicationSettings>(config.GetSection(ApplicationSettings.SectionName));
        var provider = services.BuildServiceProvider();
        using var settings = new FapCommandSettings(config, NullLogger<FapCommandSettings>.Instance);

        provider.GetRequiredService<IOptionsMonitor<ApplicationSettings>>().CurrentValue.AllowFapCommands = true;

        Assert.False(settings.AllowFapCommands);
    }

    [Fact]
    public void The_settings_page_shows_the_setting_with_its_warning()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ApplicationSettings>(_ => { });
        var discovery = new SettingsDiscoveryService(services.BuildServiceProvider(), NullLogger<SettingsDiscoveryService>.Instance);

        var section = Assert.Single(discovery.DiscoveredSettings, s => s.SettingsType == typeof(ApplicationSettings));
        var property = Assert.Single(section.Properties, p => p.PropertyName == nameof(ApplicationSettings.AllowFapCommands));
        Assert.Equal(typeof(bool), property.PropertyType);
        Assert.Equal(false, property.DefaultValue);
        Assert.Contains("REAL alarm", property.Warning);
        Assert.Contains("monitoring station", property.Warning);
    }
}
