using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Nocturne.App.Services;
using Nocturne.App.Views;
// Fully qualified at the one use site rather than aliased: this namespace also
// declares a LaunchActivatedEventArgs, and importing it makes the OnLaunched
// override ambiguous against the Microsoft.UI.Xaml type it is overriding.
using Windows.ApplicationModel.Activation;

namespace Nocturne.App;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    // Any fixed string works; it only has to be the same string every launch
    // so that AppInstance.FindOrRegisterForKey resolves to the same instance.
    private const string SingleInstanceKey = "Nocturne.App.SingleInstance";

    private Window? _window;

    /// <summary>
    /// Guards <see cref="_window"/> and <see cref="_pendingActivationPath"/>
    /// against the redirect handler, which runs on a thread-pool thread.
    /// </summary>
    private readonly object _activationLock = new();

    /// <summary>
    /// A file handed over by another process before this one had a window.
    /// </summary>
    /// <remarks>
    /// The redirect handler has to be wired the moment this instance claims the
    /// key, which is before the window is built — otherwise a second process
    /// starting in that gap finds no owner and opens a window of its own. So
    /// there is a real interval where an activation can arrive with nowhere to
    /// put it, and dropping it there means a double-clicked file does nothing at
    /// all. It is held here and collected once the window exists.
    /// </remarks>
    private string? _pendingActivationPath;

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
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Before anything else, including the log: a file association starts a
        // brand-new process on every double-click, and if one is already
        // running this one hands off the file and exits. Continuing past this
        // point commits to being the process that owns the log file and the
        // render-attempt marker for the rest of the run.
        if (TryRedirectToRunningInstance())
        {
            return;
        }

        // Before anything that can fail. The render pipeline has six stages that
        // each fail for unrelated reasons, and the log is the only way to learn
        // which one stopped on a machine nobody here can reach.
        DiagnosticLog.Start(
            Path.Combine(DataDirectory, "logs"),
            typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown");

        // Which build produced this log. A report that stops mid-way is only
        // useful if it is known which code was running, and asking the person on
        // the far end whether they installed the latest installer is not an
        // answer anyone should have to rely on.
        DiagnosticLog.Current.Write("nocturne", $"Build: {DescribeBuild()}");

        // Read before the window exists, because the window is what starts the
        // attempt this is guarding.
        RenderGuard.Failed += reason => DiagnosticLog.Current.Write("render-guard", reason);
        RenderGuard.Initialize(DataDirectory);
        if (RenderGuard.PreviousAttemptFailed)
        {
            DiagnosticLog.Current.Write(
                "nocturne",
                "the previous run did not survive building the video pipeline; " +
                "video is disabled for this run");
        }

        var main = new MainWindow();

        // Publishing the window and collecting anything that arrived while it
        // was being built happen together, under the lock the redirect handler
        // takes. Either it sees the window and posts to it, or it leaves the
        // path here and this picks it up — there is no ordering between the two
        // that loses the file.
        string? redirected;
        lock (_activationLock)
        {
            _window = main;
            redirected = _pendingActivationPath;
            _pendingActivationPath = null;
        }

        main.Activate();

        // A redirected file wins over this process's own command line: it is the
        // more recent request, and it is the one a person just double-clicked.
        string? requestedPath = redirected ?? ResolveLaunchPath();
        if (requestedPath is not null)
        {
            main.OpenOnLaunch(requestedPath);
        }
    }

    /// <summary>
    /// Reads the path this process itself was launched with, if any.
    /// </summary>
    /// <remarks>
    /// The app is unpackaged, so a file association delivers the path as a plain
    /// command-line argument rather than through an activation payload.
    /// <c>argv[0]</c> is the executable and must be skipped — treating it as a
    /// positional argument makes every double-click try to play Nocturne.exe.
    /// </remarks>
    private static string? ResolveLaunchPath() => ResolveLaunchPath(Environment.GetCommandLineArgs());

    /// <summary>
    /// Reads the path carried by an activation that was redirected here from
    /// another process's file association launch.
    /// </summary>
    /// <remarks>
    /// The app has no MSIX manifest declaring file-type activation, so the OS
    /// does not hand this process a structured file list — it launches the
    /// already-running instance's registered key the same way it would launch
    /// a fresh process: as <see cref="ExtendedActivationKind.Launch"/> carrying
    /// the raw command line in <see cref="ILaunchActivatedEventArgs.Arguments"/>.
    /// That string is parsed the same way <c>argv</c> is, then handed to the
    /// same skip-the-executable logic used for a direct launch.
    /// </remarks>
    private static string? ResolveLaunchPath(AppActivationArguments activationArgs)
    {
        if (activationArgs.Kind != ExtendedActivationKind.Launch
            || activationArgs.Data is not ILaunchActivatedEventArgs launchArgs)
        {
            // Nothing else this app registers for (protocol, startup task, ...)
            // carries a file to open.
            return null;
        }

        return ResolveLaunchPath(SplitCommandLine(launchArgs.Arguments));
    }

    /// <summary>Picks the first positional argument out of a parsed command line.</summary>
    private static string? ResolveLaunchPath(string[] arguments)
    {
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
    /// Splits a raw Win32 command-line string into <c>argv</c>-style tokens.
    /// </summary>
    /// <remarks>
    /// Delegates to the same OS parser that produces
    /// <see cref="Environment.GetCommandLineArgs"/>, so a quoted path with
    /// spaces in it splits identically on both the direct-launch path and the
    /// redirected one — hand-rolling the quoting rules here would only be a
    /// second, possibly divergent, implementation of what Windows already does.
    /// </remarks>
    private static string[] SplitCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        nint argv = CommandLineToArgvW(commandLine, out int argCount);
        if (argv == nint.Zero)
        {
            return [];
        }

        try
        {
            var result = new string[argCount];
            for (int i = 0; i < argCount; i++)
            {
                nint stringPointer = Marshal.ReadIntPtr(argv, i * nint.Size);
                result[i] = Marshal.PtrToStringUni(stringPointer) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            // The array and every string it points to come from one LocalAlloc
            // block; freeing the head is enough, but it must happen even if the
            // loop above threw.
            LocalFree(argv);
        }
    }

    /// <summary>
    /// Hands this launch to an already-running Nocturne process, if there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file association starts a brand-new process on every double-click,
    /// with nothing coordinating it against a Nocturne that is already open.
    /// Left alone that means two windows, two libmpv engines and two render
    /// pipelines competing for the same GPU, and two processes racing on
    /// <see cref="Services.RenderGuard"/>'s marker file and the diagnostic log.
    /// <see cref="AppInstance"/> is the Windows App SDK's single-instance
    /// mechanism, and unlike most of the rest of the App Lifecycle surface it
    /// is explicitly supported for unpackaged apps.
    /// </para>
    /// <para>
    /// Redirection is a convenience layered on top of a launch, not a
    /// requirement for one: any failure here — a locked named object, an SDK
    /// quirk on some machine this was never tested on — must fall through to
    /// starting normally as an independent instance rather than stop the app
    /// from opening.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> if this activation was handed off and this
    /// process is on its way out; the caller must not proceed past that point.
    /// </returns>
    private bool TryRedirectToRunningInstance()
    {
        try
        {
            AppActivationArguments activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            AppInstance keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

            if (keyInstance.IsCurrent)
            {
                // Wired here, before the window exists, because a second
                // process can call FindOrRegisterForKey and be redirected to
                // this one at any point from now on.
                keyInstance.Activated += OnActivationRedirected;
                return false;
            }

            RedirectActivationTo(activationArgs, keyInstance);
            return true;
        }
#pragma warning disable CA1031 // Redirection is a convenience; any failure must fall through to an ordinary launch.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // DiagnosticLog.Current is always non-null and safe to write to
            // even before Start() runs — it just writes nowhere until then —
            // so this is not lost, only delayed, if Start() runs afterwards.
            DiagnosticLog.Current.WriteException("single-instance", ex);
            return false;
        }
    }

    /// <summary>
    /// Sends this process's activation to the instance that already owns
    /// <see cref="SingleInstanceKey"/>, then ends this process.
    /// </summary>
    /// <remarks>
    /// <see cref="AppInstance.RedirectActivationToAsync"/> marshals the
    /// activation across apartments and needs a pumped wait to actually
    /// complete; blocking on the task directly (<c>.Result</c> or
    /// <c>.GetAwaiter().GetResult()</c>) can leave the call stuck mid-flight
    /// on the UI thread before its own message loop is running, so this
    /// process would exit having delivered nothing. Running the redirect on a
    /// pool thread and waiting on it with <c>CoWaitForMultipleObjects</c> —
    /// which pumps COM messages while it waits — is the pattern the Windows
    /// App SDK samples use for exactly this reason.
    /// </remarks>
    private static void RedirectActivationTo(AppActivationArguments activationArgs, AppInstance keyInstance)
    {
        nint eventHandle = CreateEvent(nint.Zero, bManualReset: true, bInitialState: false, lpName: null);

        Task.Run(async () =>
        {
            try
            {
                await keyInstance.RedirectActivationToAsync(activationArgs);
            }
            finally
            {
                SetEvent(eventHandle);
            }
        });

        const uint CwmoDefault = 0;
        const uint Infinite = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(CwmoDefault, Infinite, 1, [eventHandle], out _);

        // Ends the process immediately, before DiagnosticLog.Start or
        // RenderGuard.Initialize run. Both are keyed by process and belong to
        // the instance that just received this activation, not to this one —
        // letting this process fall through to them would write a log entry
        // and touch a render marker for a run that never actually starts.
        Process.GetCurrentProcess().Kill();
    }

    /// <summary>
    /// Opens a file whose activation was redirected here from another process.
    /// </summary>
    /// <remarks>
    /// Raised on a thread-pool thread, not the UI thread that owns the window —
    /// every call below has to be marshalled onto the window's own
    /// <see cref="Microsoft.UI.Dispatching.DispatcherQueue"/> or it fails with
    /// the wrong-thread error WinUI gives any cross-thread UI access.
    /// </remarks>
    private void OnActivationRedirected(object? sender, AppActivationArguments activationArgs)
    {
        // Pattern-matched into a non-nullable local rather than checked with
        // `is null` and used afterwards: the lambda below runs later, on
        // whatever thread TryEnqueue picks, and binding a genuinely
        // non-nullable `path` here removes any doubt about whether that
        // narrowing survives into a deferred closure.
        if (ResolveLaunchPath(activationArgs) is not { } path)
        {
            return;
        }

        MainWindow? main;
        lock (_activationLock)
        {
            main = _window as MainWindow;
            if (main is null)
            {
                // Still starting. OnLaunched collects this under the same lock.
                _pendingActivationPath = path;
                return;
            }
        }

        main.DispatcherQueue.TryEnqueue(() => main.OpenOnLaunch(path));
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string cmdLine, out int numArgs);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint hMem);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateEvent(nint lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(nint hEvent);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags, uint dwTimeout, uint cHandles, nint[] pHandles, out uint dwIndex);

    /// <summary>Identifies the running build beyond its version number.</summary>
    /// <remarks>
    /// The assembly version is set by hand and rarely moves, so on its own it
    /// cannot tell two builds apart. The informational version carries the
    /// commit when CI stamps it, and the executable's timestamp answers the
    /// question even when nothing stamped anything.
    /// </remarks>
    private static string DescribeBuild()
    {
        string informational = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unstamped";

        string? location = Environment.ProcessPath;
        string written = location is not null && File.Exists(location)
            ? File.GetLastWriteTimeUtc(location).ToString("u", CultureInfo.InvariantCulture)
            : "unknown";

        return $"{informational}, exe written {written} UTC, {location ?? "unknown path"}";
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
