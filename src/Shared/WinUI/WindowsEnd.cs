using System.Diagnostics;
using System.Runtime.InteropServices;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AlUmmahNow.Updates.Views;

/// <summary>
/// Every way a Windows app of ours ends comes to one <see cref="AppEnd"/>: the close button (cancelled, until the work has
/// stopped), a close that cannot be cancelled, Quit and Restart now, the process ending, and the end of the session.
/// </summary>
internal static class WindowsEnd
{
    private static AppEnd? _current;
    private static Window? _window;

    private static WndProc? _proc;
    private static IntPtr _before;

    /// <param name="begin">On the UI thread, before the work stops, each whatever the others do.</param>
    /// <param name="stop">Stops the work. Off the UI thread, and without it: that one may be blocked by the end, or its loop gone.</param>
    public static void Attach(Window window, UpdateService updates, Action[] begin, Func<CancellationToken, Task> stop)
    {
        _window = window;
        var ui = window.DispatcherQueue;
        // First: what stops no longer says that it was running
        var all = AppEnd.Each(begin.Prepend(updates.BeginEnd).ToArray());
        var end = _current = new AppEnd(() => OnUi(ui, all), stop, updates.HandOver, () => updates.IsRestartAsked, StartAgain, () => ui.TryEnqueue(Application.Current.Exit), Log);
        window.AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            Close();
        };
        // One that could not be cancelled: the loop ends, and AppStart waits for the end
        window.Closed += (_, _) => _ = end.End(EndKind.Asked);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => end.EndAndWait(EndKind.Process, AppEnd.Total);

        // WinUI tells nothing of the end of the session: the window's own messages do, on the UI thread
        _proc = (hwnd, message, w, l) =>
        {
            // WM_ENDSESSION, and it does end. Windows ends the process when this has returned, or is late
            if (message == 0x0016 && w != IntPtr.Zero) end.EndAndWait(EndKind.Session, AppEnd.SessionLimit);
            return CallWindowProcW(_before, hwnd, message, w, l);
        };
        _before = SetWindowLongPtrW(WinRT.Interop.WindowNative.GetWindowHandle(window), -4, Marshal.GetFunctionPointerForDelegate(_proc));
    }

    /// <summary>On the UI thread, as far as it answers; on this one when its loop is gone.</summary>
    private static void OnUi(DispatcherQueue ui, Action action)
    {
        if (ui.HasThreadAccess)
        {
            action();
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ui.TryEnqueue(DispatcherQueuePriority.High, () =>
        {
            action();
            done.SetResult();
        }))
        {
            action();
            return;
        }

        // Late, nothing was decided, and nothing is installed
        done.Task.Wait(TimeSpan.FromSeconds(2));
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr before, IntPtr hwnd, uint message, IntPtr w, IntPtr l);

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
