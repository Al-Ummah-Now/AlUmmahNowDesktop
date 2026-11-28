using System;
using System.IO;
using AlUmmahNow.App.Services;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class TelemetrySettingTests
{
    [Fact]
    public void Diagnostics_are_off_until_the_person_turns_them_on() =>
        Assert.False(new AppSettings().TelemetryEnabled);

    [Fact]
    public void Settings_saved_before_the_switch_existed_load_with_it_off()
    {
        var path = Path.Combine(Path.GetTempPath(), "aun-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{\"Language\":\"en\",\"Theme\":1}");
        Assert.False(new SettingsStore(path).Load().TelemetryEnabled);
    }

    [Fact]
    public void The_choice_is_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), "aun-settings-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { TelemetryEnabled = true });
        Assert.True(store.Load().TelemetryEnabled);
    }
}
