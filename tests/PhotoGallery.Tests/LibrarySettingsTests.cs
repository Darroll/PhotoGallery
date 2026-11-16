using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace PhotoGallery.Tests;

/// <summary>Schedule settings that can be changed in the app, unless a container variable fixes them.</summary>
public class LibrarySettingsTests(AppFactory app) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Backup_settings_can_be_changed_in_the_app_and_apply_at_once_while_a_container_variable_stays_in_charge()
    {
        var admin = await app.AdminAsync();
        var before = await (await admin.GetAsync("/api/admin/settings/library")).ReadAsync();
        Assert.Contains("rescanMinutes", before.GetProperty("locked").EnumerateArray().Select(x => x.GetString()));   // the test host sets RESCAN_MINUTES
        Assert.DoesNotContain("backupDaily", before.GetProperty("locked").EnumerateArray().Select(x => x.GetString()));

        var put = await admin.PutAsJsonAsync("/api/admin/settings/library", new { rescanMinutes = 5, backupHours = 12, backupDaily = 3, backupWeekly = 2 });
        Assert.True(put.IsSuccessStatusCode);
        var after = await (await admin.GetAsync("/api/admin/settings/library")).ReadAsync();
        Assert.Equal(12, after.GetProperty("backupHours").GetInt32());
        Assert.Equal(3, after.GetProperty("backupDaily").GetInt32());
        Assert.Equal(2, after.GetProperty("backupWeekly").GetInt32());
        Assert.Equal(0, after.GetProperty("rescanMinutes").GetInt32());   // fixed by the container, so the request did not change it
    }

    [Fact]
    public async Task Nonsense_values_are_pulled_into_range()
    {
        var admin = await app.AdminAsync();
        await admin.PutAsJsonAsync("/api/admin/settings/library", new { backupHours = 100000, backupDaily = 0, backupWeekly = -4 });
        var d = await (await admin.GetAsync("/api/admin/settings/library")).ReadAsync();
        Assert.Equal(720, d.GetProperty("backupHours").GetInt32());
        Assert.Equal(1, d.GetProperty("backupDaily").GetInt32());
        Assert.Equal(0, d.GetProperty("backupWeekly").GetInt32());
    }

    [Fact]
    public void Saved_values_fill_in_where_no_container_variable_is_set_and_never_override_one()
    {
        var cfg = new AppConfig(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["RESCAN_MINUTES"] = "30" }).Build());
        Assert.Equal(30, cfg.RescanMinutes);
        Assert.Equal(24, cfg.BackupHours);   // the default
        cfg.ApplyTuning(new TuningSettings { RescanMinutes = 5, BackupHours = 6 });
        Assert.Equal(30, cfg.RescanMinutes);   // the variable wins
        Assert.Equal(6, cfg.BackupHours);
    }

    [Fact]
    public async Task The_settings_are_only_for_the_admin()
    {
        Assert.False((await app.NewClient().GetAsync("/api/admin/settings/library")).IsSuccessStatusCode);
    }
}
