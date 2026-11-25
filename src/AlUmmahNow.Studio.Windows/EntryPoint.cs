namespace AlUmmahNow.Studio.WindowsApp;

public static class EntryPoint
{
    [STAThread]
    private static void Main() => AlUmmahNow.Updates.Views.AppStart.Run("Al Ummah Now Studio", "Studio", () => new App());
}
