using Keel.Application.Stats;
using Keel.Infrastructure.Settings;

namespace Keel.Infrastructure.Tests.Stats;

/// <summary>The Stats page's measurements survive a restart in settings.json (ADR 0102).</summary>
public sealed class LocalStatsSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Local_stats_round_trip_through_settings_json()
    {
        var path = _temp.File("settings.json");
        var launch = new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc);
        var cold = new ColdStartSample(1_234, 100_000, Encrypted: true, launch.AddDays(1));
        var scroll = new RegisterScrollSample(100_000, 240, 9.5, 14.25, launch.AddDays(2));
        new JsonAppSettingsStore(path).Update(s => s with { Stats = new LocalStats { FirstLaunchAt = launch, ColdStart = cold, RegisterScroll = scroll } });

        var stats = new JsonAppSettingsStore(path).Current.Stats;

        stats.FirstLaunchAt.ShouldBe(launch);
        stats.ColdStart.ShouldBe(cold);
        stats.RegisterScroll.ShouldBe(scroll);
        File.ReadAllText(path).ShouldContain("\"coldStart\"");
    }

    [Fact]
    public void Settings_without_stats_read_as_empty()
    {
        var path = _temp.File("settings.json");
        File.WriteAllText(path, """{ "version": 1, "firstRunCompleted": true }""");

        var stats = new JsonAppSettingsStore(path).Current.Stats;

        stats.ShouldNotBeNull();
        stats.FirstLaunchAt.ShouldBeNull();
        stats.ColdStart.ShouldBeNull();
    }
}
