using System.Diagnostics;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.Updates;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace AlUmmahNow.Updates.Views;

/// <summary>
/// Every way a Windows app of ours ends comes to one <see cref="AppEnd"/>: the close button (cancelled, until the work has
/// stopped), a close that cannot be cancelled, Quit and Restart now, the process ending, and the end of the session.
/// </summary>
internal static class WindowsEnd
{
    private static AppEnd? _current;
    private static Window? _window;

    /// <param name="begin">On the UI thread, before the work stops.</param>
    /// <param name="stop">Stops the work; off the UI thread, which goes on.</param>
    public static void Attach(Window window, UpdateService updates, Action begin, Func<CancellationToken, Task> stop)
    {
        _window = window;
        var ui = window.DispatcherQueue;
        var end = _current = new AppEnd(
            () =>
            {
                // First: what stops no longer says that it was running
                updates.BeginEnd();
                begin();
            },
            stop, updates.HandOver, () => updates.IsRestartAsked, StartAgain, () => ui.TryEnqueue(Application.Current.Exit), Log);
        window.AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            Close();
        };
        // One that could not be cancelled: the loop ends, and AppStart waits for the end
        window.Closed += (_, _) => _ = end.End(EndKind.Asked);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => end.EndAndWait(EndKind.Process, AppEnd.Total);
        // WinUI tells nothing of it. Windows ends the process when this has returned, or is late
        SystemEvents.SessionEnded += (_, _) => end.EndAndWait(EndKind.Session, AppEnd.SessionLimit);
    }

    /// <summary>Ends the app as its close button does. Its window goes at once; the work may take a moment to stop.</summary>
    public static void Close()
    {
        if (_current is not { } end) return;
        // Before the window goes: the player stops as it is hidden, and would not say that it was playing
        _ = end.End(EndKind.Asked);
        _window?.AppWindow.Hide();
    }

    /// <summary>The app's loop has returned: the process ends when the end is done.</summary>
    public static void WaitForEnd() => _current?.EndAndWait(EndKind.Process, AppEnd.Total);

    private static void StartAgain()
    {
        if (Environment.ProcessPath is { } path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })?.Dispose();
    }

    private static void Log(string line)
    {
#if UPDATE_TEST
        UpdateTestWatch.Log("end " + line);
#endif
    }
}
