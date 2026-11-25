namespace AlUmmahNow.Windows;

public static class EntryPoint
{
    [STAThread]
    private static void Main() => AlUmmahNow.Updates.Views.AppStart.Run("Al Ummah Now", "Ummah Now", () => new App());
}
