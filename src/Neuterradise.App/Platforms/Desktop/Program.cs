using Uno.UI.Hosting;

namespace Neuterradise.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Win32 Skia Desktop host. Media playback resources are preloaded during the (accepted) warm
        // startup so the first hover preview or Banner clip does not pay LibVLC initialization.
        // No library video is touched here.
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseWin32(builder => builder.PreloadMediaPlayer(true))
            .Build();

        host.Run();
    }
}
