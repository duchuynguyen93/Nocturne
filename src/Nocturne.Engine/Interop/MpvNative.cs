using System.Reflection;
using System.Runtime.InteropServices;

namespace Nocturne.Engine.Interop;

/// <summary>
/// Raw entry points into libmpv.
/// </summary>
/// <remarks>
/// Everything here is <see langword="internal"/> and pointer-typed on purpose.
/// Callers use <c>MpvClient</c>, which owns lifetime, string ownership, and
/// error translation; nothing outside this namespace should hold an
/// <see cref="nint"/> that came from libmpv.
/// <para>
/// String arguments are passed as explicit NUL-terminated UTF-8 rather than
/// through the marshaller. libmpv treats every string as UTF-8, while the
/// default marshalling for <see cref="string"/> on Windows is ANSI — which
/// works until the first non-ASCII path and then fails as "file not found" on a
/// file that plainly exists.
/// </para>
/// </remarks>
internal static unsafe class MpvNative
{
    /// <summary>Logical name resolved to a per-platform file by <see cref="Resolve"/>.</summary>
    private const string LibraryName = "mpv";

    /// <summary>
    /// Assemblies whose import resolver has already been installed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="NativeLibrary.SetDllImportResolver"/> is scoped to a single
    /// assembly <em>and</em> throws <see cref="InvalidOperationException"/> if
    /// called twice for the same one. Both facts matter here: the render layer
    /// declares its own P/Invokes against the logical <c>mpv</c> name, so it
    /// needs its own registration, and the bookkeeping that gives it one must
    /// not register anybody twice.
    /// </para>
    /// <para>
    /// The set and the registration call are inside one lock rather than a
    /// <see cref="Lazy{T}"/> over a list. The previous shape added the assembly
    /// to a list, registered it, and only then materialised the lazy — which,
    /// on the very first call, walked that list and registered the same
    /// assembly a second time. It never fired because the window happened to
    /// initialise the engine before the renderer, and a <see cref="Lazy{T}"/>
    /// caches the exception forever, so the whole app would have failed every
    /// later libmpv call rather than the one that tripped it.
    /// </para>
    /// </remarks>
    private static readonly HashSet<Assembly> Registered = [];

    /// <summary>
    /// Installs the import resolver for this assembly. Safe to call repeatedly.
    /// </summary>
    internal static void EnsureResolverRegistered() => Register(typeof(MpvNative).Assembly);

    /// <summary>
    /// Installs the import resolver for another assembly that P/Invokes libmpv.
    /// </summary>
    /// <remarks>
    /// Must be called before that assembly's first libmpv call, and safe to call
    /// in any order relative to <see cref="EnsureResolverRegistered"/>.
    /// </remarks>
    internal static void RegisterCallingAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        // This assembly's own registration is not implied by the caller's, and a
        // caller that reaches here first must not leave it unregistered.
        EnsureResolverRegistered();
        Register(assembly);
    }

    private static void Register(Assembly assembly)
    {
        // Held across the registration call, not merely around the set. A flag
        // released early would let a second thread see "registered" while the
        // call was still running and P/Invoke before the resolver existed — an
        // intermittent DllNotFoundException, which is the worst kind.
        lock (Registered)
        {
            if (!Registered.Add(assembly))
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        foreach (string candidate in CandidateNames())
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out nint handle))
            {
                return handle;
            }
        }

        // Returning zero lets the runtime raise DllNotFoundException naming the
        // logical name, which is less useful than saying which files were tried.
        throw new DllNotFoundException(
            $"libmpv was not found. Tried: {string.Join(", ", CandidateNames())}. " +
            "On Windows the app expects libmpv-2.dll beside the executable; run " +
            "scripts/fetch-mpv.ps1 to place it.");
    }

    private static string[] CandidateNames()
    {
        if (OperatingSystem.IsWindows())
        {
            // libmpv-2.dll is what the official Windows builds ship; mpv-2.dll is
            // what some package managers rename it to.
            //
            // mpv-1.dll — the pre-0.35 name — is deliberately NOT accepted. Its
            // mpv_opengl_init_params carries a third field, so the two-field
            // struct this code passes would leave libmpv reading eight bytes of
            // stack as an extension string. Failing to load with a clear message
            // beats crashing inside the GPU driver.
            return ["libmpv-2.dll", "mpv-2.dll"];
        }

        return OperatingSystem.IsMacOS()
            ? ["libmpv.2.dylib", "libmpv.dylib"]
            : ["libmpv.so.2", "libmpv.so"];
    }

    [DllImport(LibraryName, EntryPoint = "mpv_client_api_version")]
    internal static extern ulong ClientApiVersion();

    [DllImport(LibraryName, EntryPoint = "mpv_error_string")]
    internal static extern nint ErrorString(int error);

    [DllImport(LibraryName, EntryPoint = "mpv_free")]
    internal static extern void Free(nint data);

    [DllImport(LibraryName, EntryPoint = "mpv_create")]
    internal static extern nint Create();

    [DllImport(LibraryName, EntryPoint = "mpv_initialize")]
    internal static extern int Initialize(nint handle);

    /// <summary>
    /// Stops the core and releases the handle.
    /// </summary>
    /// <remarks>
    /// This is the destructive variant: it blocks until the core has shut down.
    /// <c>mpv_destroy</c> only detaches the client and leaves the core running,
    /// which for a single-client app means the process keeps a decoder thread
    /// and an audio device alive after the window has closed.
    /// </remarks>
    [DllImport(LibraryName, EntryPoint = "mpv_terminate_destroy")]
    internal static extern void TerminateDestroy(nint handle);

    [DllImport(LibraryName, EntryPoint = "mpv_set_option_string")]
    internal static extern int SetOptionString(nint handle, byte* name, byte* data);

    [DllImport(LibraryName, EntryPoint = "mpv_set_property_string")]
    internal static extern int SetPropertyString(nint handle, byte* name, byte* data);

    [DllImport(LibraryName, EntryPoint = "mpv_get_property_string")]
    internal static extern nint GetPropertyString(nint handle, byte* name);

    [DllImport(LibraryName, EntryPoint = "mpv_set_property")]
    internal static extern int SetProperty(nint handle, byte* name, MpvFormat format, void* data);

    [DllImport(LibraryName, EntryPoint = "mpv_get_property")]
    internal static extern int GetProperty(nint handle, byte* name, MpvFormat format, void* data);

    [DllImport(LibraryName, EntryPoint = "mpv_command")]
    internal static extern int Command(nint handle, byte** args);

    [DllImport(LibraryName, EntryPoint = "mpv_command_async")]
    internal static extern int CommandAsync(nint handle, ulong replyUserData, byte** args);

    [DllImport(LibraryName, EntryPoint = "mpv_observe_property")]
    internal static extern int ObserveProperty(nint handle, ulong replyUserData, byte* name, MpvFormat format);

    [DllImport(LibraryName, EntryPoint = "mpv_request_log_messages")]
    internal static extern int RequestLogMessages(nint handle, byte* minLevel);

    /// <summary>
    /// Waits for the next event.
    /// </summary>
    /// <returns>
    /// A pointer owned by libmpv, valid only until the next call on the same
    /// handle. The pump copies out of it immediately.
    /// </returns>
    [DllImport(LibraryName, EntryPoint = "mpv_wait_event")]
    internal static extern nint WaitEvent(nint handle, double timeoutSeconds);

    /// <summary>Interrupts a pending <see cref="WaitEvent"/> call.</summary>
    [DllImport(LibraryName, EntryPoint = "mpv_wakeup")]
    internal static extern void Wakeup(nint handle);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_create")]
    internal static extern int RenderContextCreate(out nint context, nint handle, MpvRenderParam* parameters);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_set_parameter")]
    internal static extern int RenderContextSetParameter(nint context, MpvRenderParam parameter);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_render")]
    internal static extern int RenderContextRender(nint context, MpvRenderParam* parameters);

    /// <summary>
    /// Reports whether a new frame is waiting.
    /// </summary>
    /// <returns>
    /// A bitmask; bit 0 (<c>MPV_RENDER_UPDATE_FRAME</c>) means a frame is ready
    /// to draw.
    /// </returns>
    [DllImport(LibraryName, EntryPoint = "mpv_render_context_update")]
    internal static extern ulong RenderContextUpdate(nint context);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_set_update_callback")]
    internal static extern void RenderContextSetUpdateCallback(
        nint context,
        delegate* unmanaged[Cdecl]<nint, void> callback,
        nint callbackContext);

    /// <summary>
    /// Tells libmpv that the frame it rendered has reached the display.
    /// </summary>
    /// <remarks>
    /// Must be called after every <c>Present</c>. libmpv derives its frame
    /// timing from the interval between these reports; skipping them is what
    /// turns 23.976 fps content into visible judder, because the scheduler falls
    /// back to guessing when frames land.
    /// </remarks>
    [DllImport(LibraryName, EntryPoint = "mpv_render_context_report_swap")]
    internal static extern void RenderContextReportSwap(nint context);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_free")]
    internal static extern void RenderContextFree(nint context);

    /// <summary>Bit set by <see cref="RenderContextUpdate"/> when a frame is ready.</summary>
    internal const ulong UpdateFrameFlag = 1UL;
}
