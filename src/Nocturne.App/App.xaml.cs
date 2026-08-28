using Microsoft.UI.Xaml;
using Nocturne.App.Services;
using Nocturne.App.Views;

namespace Nocturne.App;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>Initializes the application.</summary>
    public App()
    {
        InitializeComponent();

        // WinUI swallows unhandled exceptions on the UI thread into a silent
        // process exit. Without this handler an XAML or interop failure looks to
        // the user like the app simply never opened.
        UnhandledException += OnUnhandledException;

        // The handler above only sees the UI thread. The engine runs an event
        // thread and the renderer runs a render thread, and an exception
        // escaping either of those tears the process down with nothing written
        // anywhere — the same silent disappearance, from a place the WinUI hook
        // cannot observe.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    /// <summary>Where settings and logs live.</summary>
    internal static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nocturne");

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Before anything that can fail. The render pipeline has six stages that
        // each fail for unrelated reasons, and the log is the only way to learn
        // which one stopped on a machine nobody here can reach.
        DiagnosticLog.Start(
            Path.Combine(DataDirectory, "logs"),
            typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown");

        // Read before the window exists, because the window is what starts the
        // attempt this is guarding.
        RenderGuard.Initialize(DataDirectory);
        if (RenderGuard.PreviousAttemptFailed)
        {
            DiagnosticLog.Current.Write(
                "nocturne",
                "the previous run did not survive building the video pipeline; " +
                "video is disabled for this run");
        }

        _window = new MainWindow();
        _window.Activate();

        string? requestedPath = ResolveLaunchPath();
        if (requestedPath is not null && _window is MainWindow main)
        {
            main.OpenOnLaunch(requestedPath);
        }
    }

    /// <summary>
    /// Reads the path the app was launched with, if any.
    /// </summary>
    /// <remarks>
    /// The app is unpackaged, so a file association delivers the path as a plain
    /// command-line argument rather than through an activation payload.
    /// <c>argv[0]</c> is the executable and must be skipped — treating it as a
    /// positional argument makes every double-click try to play Nocturne.exe.
    /// </remarks>
    private static string? ResolveLaunchPath()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 1; i < arguments.Length; i++)
        {
            string candidate = arguments[i];
            if (!candidate.StartsWith('-') && !candidate.StartsWith('/'))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Logs a fault on a background thread before the runtime ends the process.
    /// </summary>
    /// <remarks>
    /// Nothing can be recovered from here — by the time this runs the process is
    /// already going down — but writing the reason first turns "it closed by
    /// itself" into a stack trace.
    /// </remarks>
    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception error)
        {
            DiagnosticLog.Current.WriteException("unhandled/background", error);
            return;
        }

        DiagnosticLog.Current.Write(
            "unhandled/background",
            $"a non-exception object was thrown: {e.ExceptionObject}");
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Leave Handled false: crashing loudly with a real stack is more useful
        // than continuing in a state the app cannot describe. Written to the log
        // first so a crash dump is not the only record.
        DiagnosticLog.Current.WriteException("unhandled", e.Exception);
        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {e.Exception}");
    }
}
