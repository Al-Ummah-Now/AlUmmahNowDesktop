#if UPDATE_TEST
using AlUmmahNow.App.Updates;

namespace AlUmmahNow.Updates.Views;

/// <summary>
/// CI's update test (scripts/update-test.mjs): logs what the service shows, and checks at once instead of after half a minute.
/// It closes and presses nothing: the test does, from outside, as a person does. Only with -p:UpdateTest=true.
/// </summary>
internal static class UpdateTestWatch
{
    private static bool _wasReady;

    private static string Hook(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    public static void Start(UpdateService service)
    {
        Log($"started {service.InstalledVersion} self={(service.CanInstall ? "true" : "false")}");
        var last = "";
        service.PropertyChanged += (_, _) =>
        {
            var v = service.View;
            var now = v.Step == UpdateStep.Downloading ? "" : $"{v.Stage} {v.Step} {v.Version}{(v.IsBusy ? " busy" : "")}";
            if (now.Length == 0 || now == last) return;
            last = now;
            Log(now);
            if (v.Step != UpdateStep.Ready || _wasReady || Hook("AUN_UPDATE_TEST_RECHECK") != "1") return;
            _wasReady = true;
            _ = RecheckAsync(service);
        };
        _ = BeginAsync(service);
    }

    private static async Task BeginAsync(UpdateService service)
    {
        // The window and, in the test of running work, the work are there by then
        await Task.Delay(int.TryParse(Hook("AUN_UPDATE_TEST_CHECK_MS"), out var ms) ? ms : 3000);
        await service.CheckAsync();
    }

    /// <summary>The next scheduled check, which the test cannot wait hours for.</summary>
    private static async Task RecheckAsync(UpdateService service)
    {
        await Task.Delay(8000);
        await service.CheckAsync();
        Log("rechecked " + service.View.Stage);
    }

    private static void Log(string line) => File.AppendAllText(Hook("AUN_UPDATE_TEST_LOG"), line + "\n");
}
#endif
