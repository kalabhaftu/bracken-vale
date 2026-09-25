using Microsoft.UI.Xaml;
using BrackenVale.Core;

namespace BrackenVale.App;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        UnhandledException += (_, args) => LocalAppLog.Shared.Error("ui-crash", "Unhandled WinUI exception.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LocalAppLog.Shared.Error("process-crash", "Unhandled process exception.", args.ExceptionObject as Exception ?? new InvalidOperationException(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LocalAppLog.Shared.Error("background-task", "Unobserved background-task exception.", args.Exception);
            args.SetObserved();
        };
        InitializeComponent();
        LocalAppLog.Shared.Info("app", $"Starting Bracken Vale {GetType().Assembly.GetName().Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}).");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Error("startup", "The main window failed to start.", ex);
            throw;
        }
    }
}
