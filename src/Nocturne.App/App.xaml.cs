using Microsoft.UI.Xaml;
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
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Leave Handled false: crashing loudly with a real stack is more useful
        // than continuing in a state the app cannot describe. The message is
        // written first so a crash dump is not the only record.
        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {e.Exception}");
    }
}
