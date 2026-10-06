using System.Runtime.InteropServices;
using BrackenVale.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace BrackenVale.App;

internal enum FileActivationAction
{
    Play,
    AddToQueue,
    CreatePlaylist
}

internal sealed record FileActivationRequest(FileActivationAction Action, IReadOnlyList<string> Paths);

internal static class Program
{
    private const string InstanceKey = "bracken-vale-main";
    private static readonly object ActivationGate = new();
    private static readonly Queue<FileActivationRequest> PendingActivations = new();
    private static AppInstance? _instance;
    private static DispatcherQueue? _dispatcherQueue;
    private static App? _app;

    internal static FileActivationRequest InitialActivation { get; private set; } = new(FileActivationAction.Play, []);

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var current = AppInstance.GetCurrent();
        var activation = current.GetActivatedEventArgs();
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            if (activation is not null)
            {
                try { RedirectActivationAsync(instance, activation).GetAwaiter().GetResult(); }
                catch (Exception ex) { LocalAppLog.Shared.Error("activation", "Could not forward an activation to the running Music Player window.", ex); }
            }
            return;
        }

        AppDataPaths.MigrateFromDevelopmentFolder();
        _instance = instance;
        InitialActivation = ParseActivation(activation, args);
        instance.Activated += Instance_Activated;

        Application.Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            Attach(new App(), dispatcher);
        });
    }

    private static async Task RedirectActivationAsync(AppInstance instance, AppActivationArguments activation)
        => await instance.RedirectActivationToAsync(activation);

    private static void Instance_Activated(object? sender, AppActivationArguments activation)
    {
        var request = ParseActivation(activation, []);
        App? app;
        DispatcherQueue? dispatcher;
        lock (ActivationGate)
        {
            app = _app;
            dispatcher = _dispatcherQueue;
            if (app is null || dispatcher is null)
            {
                PendingActivations.Enqueue(request);
                return;
            }
        }

        if (!dispatcher.TryEnqueue(() => app.HandleActivation(request)))
            LocalAppLog.Shared.Warning("activation", "The Music Player UI dispatcher was not available to process an activation.");
    }

    private static void Attach(App app, DispatcherQueue dispatcher)
    {
        FileActivationRequest[] pending;
        lock (ActivationGate)
        {
            _app = app;
            _dispatcherQueue = dispatcher;
            pending = PendingActivations.ToArray();
            PendingActivations.Clear();
        }

        foreach (var request in pending)
            dispatcher.TryEnqueue(() => app.HandleActivation(request));
    }

    private static FileActivationRequest ParseActivation(AppActivationArguments? activation, string[] commandLineArguments)
    {
        var paths = new List<string>();
        if (activation?.Kind == ExtendedActivationKind.File && activation.Data is IFileActivatedEventArgs fileActivation)
        {
            foreach (var file in fileActivation.Files) paths.Add(file.Path);
            return new(FileActivationAction.Play, NormalizeSupportedPaths(paths));
        }

        var arguments = commandLineArguments.Length > 0
            ? commandLineArguments
            : activation?.Data is ILaunchActivatedEventArgs launch
                ? ParseArguments(launch.Arguments)
                : [];

        var action = FileActivationAction.Play;
        var firstPath = 0;
        if (arguments.Length > 0)
        {
            action = arguments[0].ToLowerInvariant() switch
            {
                "--add-to-queue" => FileActivationAction.AddToQueue,
                "--create-playlist" => FileActivationAction.CreatePlaylist,
                _ => FileActivationAction.Play
            };
            if (arguments[0].StartsWith("--", StringComparison.Ordinal)) firstPath = 1;
        }

        for (var index = firstPath; index < arguments.Length; index++) paths.Add(arguments[index]);
        return new(action, NormalizeSupportedPaths(paths));
    }

    private static IReadOnlyList<string> NormalizeSupportedPaths(IEnumerable<string> paths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new List<string>();
        var seen = new HashSet<string>(comparer);
        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath) && LibraryScanner.IsSupportedAudioFile(fullPath) && seen.Add(fullPath)) result.Add(fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
            {
                LocalAppLog.Shared.Warning("activation", "An invalid file path was ignored during activation.", ex);
            }
        }
        return result;
    }

    private static string[] ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        var commandLine = CommandLineToArgvW("MusicPlayer.exe " + arguments, out var count);
        if (commandLine == 0) return [];
        try
        {
            var parsed = new string[Math.Max(0, count - 1)];
            for (var index = 1; index < count; index++)
                parsed[index - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(commandLine, index * IntPtr.Size)) ?? string.Empty;
            return parsed;
        }
        finally { _ = LocalFree(commandLine); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
