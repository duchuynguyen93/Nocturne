using System.Globalization;
using System.Text;

namespace Nocturne.App.Services;

/// <summary>
/// Appends a plain-text log beside the app's settings.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the machines that matter are not the machine this code is
/// written on. A first-run failure on someone else's Windows box arrives as one
/// sentence in a chat message, and the difference between "the render failed"
/// and "eglCreatePbufferFromClientBuffer returned EGL_BAD_MATCH after the
/// display was built on device 0x1F3A" is the difference between one guess and
/// one fix.
/// </para>
/// <para>
/// Deliberately not a logging framework. One file, append-only, no levels to
/// configure, no dependency: whatever else has gone wrong, writing a line must
/// not be the thing that also fails.
/// </para>
/// </remarks>
public sealed class DiagnosticLog
{
    // object, not System.Threading.Lock: the app targets net8.0 and Lock is net9+.
    private static readonly object Gate = new();

    private readonly string? _path;
    private readonly StreamWriter? _writer;

    private DiagnosticLog(string? path, StreamWriter? writer)
    {
        _path = path;
        _writer = writer;
    }

    /// <summary>The log for this process. Never null; may be writing nowhere.</summary>
    public static DiagnosticLog Current { get; private set; } = new(path: null, writer: null);

    /// <summary>Full path of the file, or null when no file could be opened.</summary>
    public string? Path => _path;

    /// <summary>
    /// Opens the log for this run and makes it <see cref="Current"/>.
    /// </summary>
    /// <remarks>
    /// A failure here is swallowed. An app that refuses to start because it
    /// could not open its own log file has turned a diagnostic aid into a fault.
    /// </remarks>
    public static void Start(string directory, string appVersion)
    {
        string? path = null;
        StreamWriter? writer = null;
        try
        {
            Directory.CreateDirectory(directory);
            // The process id is in the name because a file association starts a
            // new process per double-click. Two of them launched within the same
            // second would otherwise open the same file with two independent
            // writers, and appending from two processes is not atomic — the
            // lines interleave mid-sentence, in the one file whose whole purpose
            // is to be read after something went wrong.
            path = System.IO.Path.Combine(
                directory,
                $"nocturne-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");

            // Keep the last few runs and no more. Nobody reads the tenth-oldest
            // log, and an unbounded folder in AppData is a slow leak.
            PruneOldLogs(directory, keep: 5);

            // Held open for the life of the process rather than reopened per
            // line. libmpv logs at verbose level, so the startup sequence alone
            // is hundreds of lines, and an open-append-close for each one turns
            // the diagnostic into the slowest thing in the launch path.
            //
            // FileShare.ReadWrite so the file can be read — and sent — while the
            // app is still running. AutoFlush so each line reaches the operating
            // system as it is written: the whole purpose here is to survive a
            // process that is about to be killed without warning, and a buffer
            // inside that process would go down with it.
            writer = new StreamWriter(
                new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite),
                Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }
#pragma warning disable CA1031 // Losing the log must never stop the app.
        catch (Exception)
#pragma warning restore CA1031
        {
            writer?.Dispose();
            writer = null;
            path = null;
        }

        Current = new DiagnosticLog(path, writer);
        Current.Write("nocturne", $"Nocturne {appVersion} starting");
        Current.Write("nocturne", $"OS: {Environment.OSVersion}, {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Current.Write("nocturne", $"Log: {path ?? "(none — could not open a file)"}");
    }

    /// <summary>Writes one line, prefixed with a timestamp and a subsystem tag.</summary>
    public void Write(string source, string message)
    {
        if (_writer is null)
        {
            return;
        }

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss.fff}  {source,-14}  {message}");

        try
        {
            // libmpv's event thread, the render thread and the UI thread all
            // write here.
            lock (Gate)
            {
                _writer.WriteLine(line);
            }
        }
#pragma warning disable CA1031 // Same again: a failed write is not worth an exception.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing useful to do. Reporting a logging failure through the log
            // is circular, and through the UI is noise.
        }
    }

    /// <summary>Writes an exception with its full chain.</summary>
    public void WriteException(string source, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Write(source, error.ToString());
    }

    private static void PruneOldLogs(string directory, int keep)
    {
        var stale = new DirectoryInfo(directory)
            .GetFiles("nocturne-*.log")
            .OrderByDescending(file => file.CreationTimeUtc)
            .Skip(keep);

        foreach (FileInfo file in stale)
        {
            try
            {
                file.Delete();
            }
#pragma warning disable CA1031 // Pruning must never be the reason logging stops.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Held open by something else, or marked read-only, or sitting
                // in a synced folder that objects. Catching only IOException let
                // an UnauthorizedAccessException escape the loop and fail the
                // whole of Start — which set the writer to null and ran the
                // entire session with no log at all, in the session that most
                // needed one.
            }
        }
    }
}
