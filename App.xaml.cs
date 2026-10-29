using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace CHZMFRAMEUNLOCK1;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Никогда не падаем из-за отсутствующего мода / прав / железа.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            Log(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log(args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private static void Log(Exception? exception)
    {
        if (exception is null) return;

        try
        {
            System.Diagnostics.Debug.WriteLine("[CHZM] " + exception);
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "chzm-error.log"),
                $"{DateTime.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }
}
