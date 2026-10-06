using Microsoft.UI.Xaml;
using BrackenVale.Core;

namespace BrackenVale.App;

public partial class App : Application
{
    private Window? _window;
    private readonly Queue<FileActivationRequest> _pendingFileActivations = new();
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
        LocalAppLog.Shared.Info("app", $"Starting Music Player {GetType().Assembly.GetName().Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}).");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window ??= new MainWindow();
            _window.Activate();
            HandleActivation(Program.InitialActivation);
            while (_pendingFileActivations.TryDequeue(out var pending)) HandleActivation(pending);
        }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Error("startup", "The main window failed to start.", ex);
            throw;
        }
    }

    internal void HandleActivation(FileActivationRequest request)
    {
        if (_window is null)
        {
            _pendingFileActivations.Enqueue(request);
            return;
        }

        _window.Activate();
        if (request.Paths.Count == 0) return;

        var mainWindow = (MainWindow)_window;
        switch (request.Action)
        {
            case FileActivationAction.AddToQueue:
                _ = mainWindow.AddExternalFilesToQueueAsync(request.Paths);
                break;
            case FileActivationAction.CreatePlaylist:
                _ = mainWindow.CreatePlaylistFromExternalFilesAsync(request.Paths);
                break;
            default:
                _ = mainWindow.PlayExternalFilesAsync(request.Paths);
                break;
        }
    }
}
