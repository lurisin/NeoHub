using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoHub.Services;
using NeoHub.Services.Settings;
using NeoHub.Tests.Infrastructure;

namespace NeoHub.Tests;

/// <summary>
/// Only the saved settings file (userSettings.json) can turn FAP commands on in NeoHub's wiring: not an environment
/// variable, a command-line argument or another configuration source.
/// </summary>
public class FapSettingSourceTests
{
    private static bool Allowed(ProgramHost host) => host.Services.GetRequiredService<IFapCommandSettings>().AllowFapCommands;

    private static Dictionary<string, string?> Env(string value) => new() { ["Application__AllowFapCommands"] = value };

    private static (CaptureLoggerProvider Log, ILogger<FapCommandSettings> Logger) Capture()
    {
        var provider = new CaptureLoggerProvider();
        return (provider, LoggerFactory.Create(b => b.AddProvider(provider)).CreateLogger<FapCommandSettings>());
    }

    [Fact]
    public void An_environment_variable_does_not_turn_FAP_on_when_the_file_lacks_the_key()
    {
        using (var host = ProgramHost.Build("""{"Application":{"ListenPort":3072}}""", Env("true")))
            Assert.False(Allowed(host));
        using (var host = ProgramHost.Build(null, Env("true")))
            Assert.False(Allowed(host));
    }

    [Fact]
    public void A_command_line_argument_does_not_turn_FAP_on()
    {
        using var host = ProgramHost.Build("""{"Application":{"ListenPort":3072}}""", args: new[] { "--Application:AllowFapCommands=true" });
        Assert.False(Allowed(host));
    }

    [Fact]
    public void The_saved_file_decides_whatever_the_environment_says()
    {
        using (var host = ProgramHost.Build("""{"Application":{"AllowFapCommands":false}}""", Env("true")))
            Assert.False(Allowed(host));
        // Positive control: the saved setting turns it on, also against an environment variable.
        using (var host = ProgramHost.Build("""{"Application":{"AllowFapCommands":true}}""", Env("false")))
            Assert.True(Allowed(host));
        using (var host = ProgramHost.Build("""{"Application":{"AllowFapCommands":true}}"""))
            Assert.True(Allowed(host));
    }

    [Fact]
    public async Task Saving_the_setting_still_applies_it()
    {
        using var host = ProgramHost.Build("""{"Application":{"ListenPort":3072}}""", Env("true"));
        var persistence = host.Services.GetRequiredService<ISettingsPersistenceService>();
        Assert.False(Allowed(host));

        await persistence.SaveSettingsAsync(typeof(ApplicationSettings), new ApplicationSettings { AllowFapCommands = true });
        Assert.True(Allowed(host));
        await persistence.SaveSettingsAsync(typeof(ApplicationSettings), new ApplicationSettings { AllowFapCommands = false });
        Assert.False(Allowed(host));
    }

    [Fact]
    public void Without_the_settings_file_provider_FAP_stays_off()
    {
        var (log, logger) = Capture();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [FapCommandSettings.ConfigurationKey] = "true" })
            .Build();
        using var settings = new FapCommandSettings(configuration, logger, "userSettings.json");
        Assert.False(settings.AllowFapCommands);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("not part of the configuration"));
    }

    [Fact]
    public void A_value_from_another_source_is_logged_as_ignored()
    {
        var dir = Directory.CreateTempSubdirectory("neohub-fap-source-");
        try
        {
            var file = Path.Combine(dir.FullName, "userSettings.json");
            File.WriteAllText(file, """{"Application":{"ListenPort":3072}}""");
            var (log, logger) = Capture();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [FapCommandSettings.ConfigurationKey] = "true" })
                .AddJsonFile(file, optional: true, reloadOnChange: false)
                .Build();
            using var settings = new FapCommandSettings(configuration, logger, "userSettings.json");
            Assert.False(settings.AllowFapCommands);
            Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("ignored"));

            // The file's own value counts.
            File.WriteAllText(file, """{"Application":{"AllowFapCommands":true}}""");
            configuration.Reload();
            Assert.True(settings.AllowFapCommands);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
