using System.Runtime.InteropServices;
using Nocturne.Engine.Interop;

namespace Nocturne.Engine.Client;

/// <summary>
/// A safe wrapper around one libmpv handle.
/// </summary>
/// <remarks>
/// <para>
/// libmpv is thread-safe for commands and property access, but the handle must
/// not be touched after <c>mpv_terminate_destroy</c> begins. That is the whole
/// difficulty of wrapping it: a UI thread can be halfway through a property
/// write while the window closes. Every call therefore takes a reader lock on
/// <see cref="_lifetimeLock"/> and disposal takes the writer lock, so shutdown
/// waits for in-flight calls instead of pulling the handle out from under them.
/// </para>
/// <para>
/// Events are pumped on a dedicated thread rather than polled from the UI. The
/// pump raises .NET events on that thread; callers that touch UI state are
/// responsible for marshalling. <c>PlayerEngine</c> does that in one place.
/// </para>
/// </remarks>
public sealed unsafe class MpvClient : IDisposable
{
    private readonly ReaderWriterLockSlim _lifetimeLock = new(LockRecursionPolicy.NoRecursion);
    private readonly Thread _eventThread;
    private readonly CancellationTokenSource _pumpCancellation = new();

    private nint _handle;
    private bool _disposed;

    /// <summary>
    /// Creates and initializes a libmpv handle.
    /// </summary>
    /// <param name="options">
    /// Options applied before <c>mpv_initialize</c>. Some options — notably
    /// <c>vo</c>, <c>hwdec</c>, and <c>gpu-api</c> — are only read at
    /// initialization, so setting them afterwards silently does nothing.
    /// </param>
    /// <exception cref="MpvException">The handle could not be created.</exception>
    public MpvClient(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        MpvNative.EnsureResolverRegistered();

        _handle = MpvNative.Create();
        if (_handle == nint.Zero)
        {
            throw new MpvException(
                "mpv_create returned null. The libmpv build is present but refused to " +
                "allocate a client handle, which usually means an incompatible version.");
        }

        try
        {
            foreach ((string name, string value) in options)
            {
                SetOptionCore(name, value);
            }

            int result = MpvNative.Initialize(_handle);
            ThrowIfError(result, "mpv_initialize");
        }
        catch
        {
            // The handle exists but is unusable. Release it here rather than
            // leaving a half-built client for the caller to dispose.
            MpvNative.TerminateDestroy(_handle);
            _handle = nint.Zero;
            throw;
        }

        _eventThread = new Thread(PumpEvents)
        {
            Name = "Nocturne mpv events",
            IsBackground = true,
        };
        _eventThread.Start();
    }

    /// <summary>Raised when an observed property changes.</summary>
    public event EventHandler<MpvPropertyChangedEventArgs>? PropertyChanged;

    /// <summary>Raised once a file's tracks and duration are known.</summary>
    public event EventHandler? FileLoaded;

    /// <summary>Raised when a playlist entry stops, for any reason.</summary>
    public event EventHandler<MpvEndFileEventArgs>? EndFile;

    /// <summary>Raised when playback resumes after a seek or an initial load.</summary>
    public event EventHandler? PlaybackRestart;

    /// <summary>Raised when libmpv emits a log line at or above the requested level.</summary>
    public event EventHandler<MpvLogEventArgs>? LogMessage;

    /// <summary>Raised when the core shuts down, whether or not the app asked it to.</summary>
    public event EventHandler? Shutdown;

    /// <summary>The native handle, for the render layer only.</summary>
    /// <remarks>
    /// Exposed because <c>mpv_render_context_create</c> needs it. Callers must
    /// not retain it past the lifetime of this client.
    /// </remarks>
    public nint Handle => _handle;

    /// <summary>The libmpv client API version, as major/minor.</summary>
    public static (int Major, int Minor) ApiVersion
    {
        get
        {
            MpvNative.EnsureResolverRegistered();
            ulong version = MpvNative.ClientApiVersion();
            return ((int)(version >> 16), (int)(version & 0xFFFF));
        }
    }

    /// <summary>Runs a command, as the argument list libmpv's own IPC accepts.</summary>
    /// <param name="arguments">
    /// Command name followed by its arguments, for example
    /// <c>["loadfile", path, "replace"]</c>.
    /// </param>
    public void Command(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0)
        {
            throw new ArgumentException("A command needs at least a name.", nameof(arguments));
        }

        WithHandle(handle =>
        {
            // libmpv wants a NULL-terminated char** — one extra slot past the
            // arguments, not one per argument.
            var buffers = new Utf8Buffer[arguments.Length];
            byte** argv = stackalloc byte*[arguments.Length + 1];
            try
            {
                for (int i = 0; i < arguments.Length; i++)
                {
                    buffers[i] = Utf8.Allocate(arguments[i]);
                    argv[i] = buffers[i].Pointer;
                }

                argv[arguments.Length] = null;
                ThrowIfError(MpvNative.Command(handle, argv), $"mpv command '{arguments[0]}'");
            }
            finally
            {
                foreach (Utf8Buffer buffer in buffers)
                {
                    buffer.Dispose();
                }
            }
        });
    }

    /// <summary>Sets an option that libmpv only reads before initialization.</summary>
    public void SetOption(string name, string value) =>
        WithHandle(_ => SetOptionCore(name, value));

    /// <summary>Writes a numeric property.</summary>
    public void SetProperty(string name, double value) => WithHandle(handle =>
    {
        using Utf8Buffer nameBuffer = Utf8.Allocate(name);
        double local = value;
        ThrowIfError(
            MpvNative.SetProperty(handle, nameBuffer.Pointer, MpvFormat.Double, &local),
            $"set property '{name}'");
    });

    /// <summary>Writes a boolean property.</summary>
    public void SetProperty(string name, bool value) => WithHandle(handle =>
    {
        using Utf8Buffer nameBuffer = Utf8.Allocate(name);

        // MPV_FORMAT_FLAG is an int, not a C99 bool. Passing a one-byte value
        // leaves three bytes of stack garbage that libmpv reads as true.
        int local = value ? 1 : 0;
        ThrowIfError(
            MpvNative.SetProperty(handle, nameBuffer.Pointer, MpvFormat.Flag, &local),
            $"set property '{name}'");
    });

    /// <summary>Writes a string property.</summary>
    public void SetProperty(string name, string value) => WithHandle(handle =>
    {
        using Utf8Buffer nameBuffer = Utf8.Allocate(name);
        using Utf8Buffer valueBuffer = Utf8.Allocate(value);
        ThrowIfError(
            MpvNative.SetPropertyString(handle, nameBuffer.Pointer, valueBuffer.Pointer),
            $"set property '{name}'");
    });

    /// <summary>
    /// Reads a numeric property.
    /// </summary>
    /// <returns>
    /// The value, or <see langword="null"/> when the property exists but has no
    /// value right now — which is the normal state of <c>duration</c> before a
    /// file is open, not an error.
    /// </returns>
    public double? GetDouble(string name) => WithHandle(handle =>
    {
        using Utf8Buffer nameBuffer = Utf8.Allocate(name);
        double value = 0;
        int result = MpvNative.GetProperty(handle, nameBuffer.Pointer, MpvFormat.Double, &value);
        if (result == (int)MpvError.PropertyUnavailable)
        {
            return (double?)null;
        }

        ThrowIfError(result, $"read property '{name}'");
        return value;
    });

    /// <summary>Reads a boolean property, or <see langword="null"/> when unavailable.</summary>
    public bool? GetFlag(string name) => WithHandle(handle =>
    {
        using Utf8Buffer nameBuffer = Utf8.Allocate(name);
        int value = 0;
        int result = MpvNative.GetProperty(handle, nameBuffer.Pointer, MpvFormat.Flag, &value);
        if (result == (int)MpvError.PropertyUnavailable)
        {
            return (bool?)null;
        }

        ThrowIfError(result, $"read property '{name}'");
        return value != 0;
    });

    /// <summary>Reads a string property, or <see langword="null"/> when unavailable.</summary>
    public string? GetString(string name) => WithHandle(handle =>
    {
        using Utf8Buffer nameBuffer = Utf8.Allocate(name);
        return Utf8.ReadAndFree(MpvNative.GetPropertyString(handle, nameBuffer.Pointer));
    });

    /// <summary>
    /// Asks libmpv to raise <see cref="PropertyChanged"/> for a property.
    /// </summary>
    /// <param name="name">Property name, such as <c>time-pos</c>.</param>
    /// <param name="format">Shape the value should be delivered in.</param>
    public void ObserveProperty(string name, MpvFormat format) => WithHandle(handle =>
    {
        using Utf8Buffer nameBuffer = Utf8.Allocate(name);
        ThrowIfError(
            MpvNative.ObserveProperty(handle, 0, nameBuffer.Pointer, format),
            $"observe property '{name}'");
    });

    /// <summary>Turns on libmpv logging at or above <paramref name="minimumLevel"/>.</summary>
    /// <param name="minimumLevel">
    /// One of libmpv's level names: <c>no</c>, <c>fatal</c>, <c>error</c>,
    /// <c>warn</c>, <c>info</c>, <c>v</c>, <c>debug</c>, <c>trace</c>.
    /// </param>
    public void RequestLogMessages(string minimumLevel) => WithHandle(handle =>
    {
        using Utf8Buffer levelBuffer = Utf8.Allocate(minimumLevel);
        ThrowIfError(MpvNative.RequestLogMessages(handle, levelBuffer.Pointer), "request log messages");
    });

    /// <inheritdoc />
    public void Dispose()
    {
        // Take the writer lock first so any in-flight command finishes against a
        // live handle. Everything after this point sees _disposed and returns.
        _lifetimeLock.EnterWriteLock();
        nint handle;
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            handle = _handle;
            _handle = nint.Zero;
        }
        finally
        {
            _lifetimeLock.ExitWriteLock();
        }

        _pumpCancellation.Cancel();

        if (handle != nint.Zero)
        {
            // Wake the pump so it observes cancellation instead of sitting in
            // mpv_wait_event until its timeout expires.
            MpvNative.Wakeup(handle);
        }

        // Bounded join: a wedged decoder must not stop the window from closing.
        // The thread is a background thread, so a timeout here cannot keep the
        // process alive either way.
        if (!_eventThread.Join(TimeSpan.FromSeconds(2)))
        {
            LogMessage?.Invoke(this, new MpvLogEventArgs(
                "nocturne", "warn", "mpv event pump did not stop within two seconds."));
        }

        if (handle != nint.Zero)
        {
            MpvNative.TerminateDestroy(handle);
        }

        _pumpCancellation.Dispose();
        _lifetimeLock.Dispose();
    }

    private void SetOptionCore(string name, string value)
    {
        using Utf8Buffer nameBuffer = Utf8.Allocate(name);
        using Utf8Buffer valueBuffer = Utf8.Allocate(value);
        ThrowIfError(
            MpvNative.SetOptionString(_handle, nameBuffer.Pointer, valueBuffer.Pointer),
            $"set option '{name}'");
    }

    private void PumpEvents()
    {
        CancellationToken token = _pumpCancellation.Token;

        while (!token.IsCancellationRequested)
        {
            nint eventPointer;
            _lifetimeLock.EnterReadLock();
            try
            {
                if (_disposed || _handle == nint.Zero)
                {
                    return;
                }

                // A finite timeout rather than -1: it bounds how long disposal
                // can wait even if the wakeup is lost, and costs one wakeup per
                // second while idle.
                eventPointer = MpvNative.WaitEvent(_handle, 1.0);
            }
            finally
            {
                _lifetimeLock.ExitReadLock();
            }

            if (eventPointer == nint.Zero)
            {
                continue;
            }

            // Copy out before releasing: the pointer is only valid until the
            // next mpv_wait_event on this handle.
            MpvEvent nativeEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);

            if (nativeEvent.EventId == MpvEventId.None)
            {
                continue;
            }

            try
            {
                DispatchEvent(nativeEvent);
            }
#pragma warning disable CA1031 // A handler that throws must not kill the pump.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogMessage?.Invoke(this, new MpvLogEventArgs(
                    "nocturne", "error", $"Event handler threw: {ex}"));
            }

            if (nativeEvent.EventId == MpvEventId.Shutdown)
            {
                return;
            }
        }
    }

    private void DispatchEvent(MpvEvent nativeEvent)
    {
        switch (nativeEvent.EventId)
        {
            case MpvEventId.PropertyChange:
                DispatchPropertyChange(nativeEvent.Data);
                break;

            case MpvEventId.FileLoaded:
                FileLoaded?.Invoke(this, EventArgs.Empty);
                break;

            case MpvEventId.PlaybackRestart:
                PlaybackRestart?.Invoke(this, EventArgs.Empty);
                break;

            case MpvEventId.EndFile:
                DispatchEndFile(nativeEvent.Data);
                break;

            case MpvEventId.LogMessage:
                DispatchLogMessage(nativeEvent.Data);
                break;

            case MpvEventId.Shutdown:
                Shutdown?.Invoke(this, EventArgs.Empty);
                break;

            default:
                // Every other event id is deliberately ignored; libmpv adds new
                // ones between releases and an exhaustive switch would turn an
                // upgrade into a crash.
                break;
        }
    }

    private void DispatchPropertyChange(nint data)
    {
        if (data == nint.Zero)
        {
            return;
        }

        MpvEventProperty property = Marshal.PtrToStructure<MpvEventProperty>(data);
        string? name = Utf8.Read(property.Name);
        if (name is null)
        {
            return;
        }

        // A null Data means the property has no value at the moment — the normal
        // state of time-pos between files. Subscribers get a null value rather
        // than a stale one held over from the previous file.
        object? value = null;
        if (property.Data != nint.Zero)
        {
            value = property.Format switch
            {
                MpvFormat.Double => Marshal.PtrToStructure<double>(property.Data),
                MpvFormat.Flag => Marshal.ReadInt32(property.Data) != 0,
                MpvFormat.Int64 => Marshal.ReadInt64(property.Data),

                // For MPV_FORMAT_STRING, Data is a char** — the address of the
                // pointer, not the text. One dereference short and this reads a
                // pointer value as characters.
                MpvFormat.String or MpvFormat.OsdString => Utf8.Read(Marshal.ReadIntPtr(property.Data)),
                _ => null,
            };
        }

        PropertyChanged?.Invoke(this, new MpvPropertyChangedEventArgs(name, property.Format, value));
    }

    private void DispatchEndFile(nint data)
    {
        if (data == nint.Zero)
        {
            return;
        }

        MpvEventEndFile endFile = Marshal.PtrToStructure<MpvEventEndFile>(data);
        EndFile?.Invoke(this, new MpvEndFileEventArgs(endFile.Reason, (MpvError)endFile.Error));
    }

    private void DispatchLogMessage(nint data)
    {
        if (data == nint.Zero)
        {
            return;
        }

        MpvEventLogMessage message = Marshal.PtrToStructure<MpvEventLogMessage>(data);
        LogMessage?.Invoke(this, new MpvLogEventArgs(
            Utf8.Read(message.Prefix) ?? string.Empty,
            Utf8.Read(message.Level) ?? string.Empty,
            (Utf8.Read(message.Text) ?? string.Empty).TrimEnd('\n')));
    }

    private void WithHandle(Action<nint> action)
    {
        _lifetimeLock.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            action(_handle);
        }
        finally
        {
            _lifetimeLock.ExitReadLock();
        }
    }

    private T WithHandle<T>(Func<nint, T> action)
    {
        _lifetimeLock.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return action(_handle);
        }
        finally
        {
            _lifetimeLock.ExitReadLock();
        }
    }

    private static void ThrowIfError(int result, string operation)
    {
        if (result < 0)
        {
            throw new MpvException((MpvError)result, operation);
        }
    }
}
