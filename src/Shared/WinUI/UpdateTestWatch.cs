#if UPDATE_TEST
using AlUmmahNow.App.Updates;

namespace AlUmmahNow.Updates.Views;

/// <summary>
/// CI's update test (scripts/update-test.mjs): logs what the service shows, checks at once instead of after half a minute, and
/// closes the app (or presses Restart now) when an update is ready. Only with -p:UpdateTest=true.
/// </summary>
internal static class UpdateTestWatch
{
    private static string Hook(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    public static void Start(UpdateService service, Action close)
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
            if (v.Step == UpdateStep.Ready && !_isEnding) _ = EndAsync(service, close);
        };
        _ = BeginAsync(service);
    }

    private static bool _isEnding;

    private static async Task BeginAsync(UpdateService service)
    {
        // The window and, in the test of running work, the work are there by then
        await Task.Delay(int.TryParse(Hook("AUN_UPDATE_TEST_CHECK_MS"), out var ms) ? ms : 3000);
        await service.CheckAsync();
    }

    private static async Task EndAsync(UpdateService service, Action close)
    {
        _isEnding = true;
        await Task.Delay(int.TryParse(Hook("AUN_UPDATE_TEST_EXIT_MS"), out var ms) ? ms : 1000);
        if (Hook("AUN_UPDATE_TEST_RECHECK") == "1")
        {
            await service.CheckAsync(isAsked: true);
            Log("rechecked " + service.View.Stage);
        }

        if (Hook("AUN_UPDATE_TEST_RESTART") == "1")
        {
            Log("pressed " + service.View.CanBegin);
            service.Begin();
            if (service.View.CanBegin) return;
        }

        Log("closing");
        close();
    }

    private static void Log(string line) => File.AppendAllText(Hook("AUN_UPDATE_TEST_LOG"), line + "\n");
}
#endif
