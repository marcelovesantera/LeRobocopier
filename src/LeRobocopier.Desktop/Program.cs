using Avalonia;

namespace LeRobocopier.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<App>()
        .UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
