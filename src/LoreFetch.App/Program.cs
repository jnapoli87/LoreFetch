using Avalonia;

namespace LoreFetch.App;

// Bare entry point. The real app shell — startup composition, the main
// window, the scan pipeline wiring — lives in App.axaml.cs's
// OnFrameworkInitializationCompleted and in AppComposition (A1).
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
