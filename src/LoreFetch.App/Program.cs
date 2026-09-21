using Avalonia;

namespace LoreFetch.App;

// Bare entry point. Stream A's package A1 owns the real app shell (startup
// composition, the main window, the scan pipeline wiring) and replaces this.
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
