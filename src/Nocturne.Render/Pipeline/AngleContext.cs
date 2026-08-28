using System.Runtime.InteropServices;
using System.Text;
using Nocturne.Render.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Nocturne.Render.Pipeline;

/// <summary>
/// An OpenGL ES context that renders straight into Direct3D 11 textures.
/// </summary>
/// <remarks>
/// <para>
/// The context and the <see cref="ID3D11Device"/> the rest of the pipeline draws
/// with are created together and owned here, because which of the two comes
/// first is not fixed. There are two ways to end up with ANGLE and Direct3D
/// sharing one device, and which one is available depends on the ANGLE build:
/// </para>
/// <list type="number">
/// <item>
/// The app creates the device and ANGLE adopts it, through
/// <c>eglCreateDeviceANGLE</c> and <c>EGL_PLATFORM_DEVICE_EXT</c>. Preferred,
/// because the app chooses the feature level and the creation flags.
/// </item>
/// <item>
/// ANGLE creates the device and the app adopts it, through
/// <c>EGL_PLATFORM_ANGLE_ANGLE</c> and <c>EGL_EXT_device_query</c>. Always
/// available — it is how every ANGLE consumer works by default — and it reaches
/// the same place: one device, no cross-device synchronisation per frame.
/// </item>
/// </list>
/// <para>
/// What must not happen is two devices. Every frame would then need a shared
/// handle and a keyed mutex, and that wait lands inside the presentation
/// interval where it is most visible.
/// </para>
/// </remarks>
internal sealed unsafe class AngleContext : IDisposable
{
    /// <summary>Guards <c>eglCreateDeviceANGLE</c>.</summary>
    private const string DeviceCreationExtension = "EGL_ANGLE_device_creation";

    /// <summary>Guards the D3D11 argument to <c>eglCreateDeviceANGLE</c>.</summary>
    private const string DeviceCreationD3D11Extension = "EGL_ANGLE_device_creation_d3d11";

    /// <summary>Guards <c>EGL_PLATFORM_DEVICE_EXT</c>.</summary>
    private const string PlatformDeviceExtension = "EGL_EXT_platform_device";

    /// <summary>Guards <c>EGL_PLATFORM_ANGLE_ANGLE</c>.</summary>
    private const string PlatformAngleExtension = "EGL_ANGLE_platform_angle";

    /// <summary>Guards the D3D11 backend selector.</summary>
    private const string PlatformAngleD3DExtension = "EGL_ANGLE_platform_angle_d3d";

    /// <summary>Guards <c>eglQueryDisplayAttribEXT</c> and <c>eglQueryDeviceAttribEXT</c>.</summary>
    private const string DeviceQueryExtension = "EGL_EXT_device_query";

    private nint _display;
    private nint _config;
    private nint _context;
    private nint _eglDevice;

    /// <summary>Whether <see cref="_eglDevice"/> is ours to release.</summary>
    private readonly bool _ownsEglDevice;

    private ID3D11Device? _device;
    private bool _disposed;

    private AngleContext(
        nint display,
        nint config,
        nint context,
        nint eglDevice,
        bool ownsEglDevice,
        ID3D11Device device)
    {
        _display = display;
        _config = config;
        _context = context;
        _eglDevice = eglDevice;
        _ownsEglDevice = ownsEglDevice;
        _device = device;
    }

    /// <summary>The EGL display, needed when creating surfaces.</summary>
    internal nint Display => _display;

    /// <summary>The chosen EGL config, needed when wrapping a texture.</summary>
    internal nint Config => _config;

    /// <summary>
    /// The Direct3D 11 device this context renders through.
    /// </summary>
    /// <remarks>
    /// Owned by this object. Callers use it and must not dispose it.
    /// </remarks>
    internal ID3D11Device Device => _device
        ?? throw new ObjectDisposedException(nameof(AngleContext));

    /// <summary>
    /// Builds an ANGLE context and the Direct3D device that goes with it.
    /// </summary>
    /// <exception cref="RenderInitializationException">ANGLE could not be set up.</exception>
    internal static AngleContext Create(Action<string>? trace = null)
    {
        void Step(string message) => trace?.Invoke(message);

        // Before anything else, and before any EGL call that could fail: ask the
        // library what it can actually do.
        //
        // This is not defensive tidiness. ANGLE's extension entry points are
        // exported unconditionally, but an entry point whose backend was not
        // compiled in does not return an error — it reaches an UNREACHABLE() and
        // aborts the process. An abort inside a native library is not an
        // exception; nothing in the managed world catches it, and the app simply
        // vanishes the moment its window would have appeared. Reading the client
        // extension string first is the only way to ask the question safely.
        string? clientExtensions = ReadClientExtensions(Step);

        bool canAdoptAppDevice =
            Egl.HasExtension(clientExtensions, DeviceCreationExtension) &&
            Egl.HasExtension(clientExtensions, DeviceCreationD3D11Extension) &&
            Egl.HasExtension(clientExtensions, PlatformDeviceExtension);

        bool canUseAnglePlatform =
            Egl.HasExtension(clientExtensions, PlatformAngleExtension) &&
            Egl.HasExtension(clientExtensions, PlatformAngleD3DExtension);

        Step($"device adoption {(canAdoptAppDevice ? "available" : "unavailable")}, " +
             $"ANGLE D3D11 platform {(canUseAnglePlatform ? "available" : "unavailable")}");

        if (canAdoptAppDevice)
        {
            try
            {
                return CreateOnAppDevice(trace);
            }
            catch (RenderInitializationException error) when (canUseAnglePlatform)
            {
                // Reported, not swallowed: the second path succeeding does not
                // make the first path's failure uninteresting, and it is the
                // line that explains why the app is on the fallback.
                Step($"device adoption failed, falling back: {error.Message}");
            }
        }

        if (!canUseAnglePlatform)
        {
            throw new RenderInitializationException(
                "This ANGLE build offers neither device adoption " +
                $"({DeviceCreationD3D11Extension}) nor the Direct3D 11 platform " +
                $"({PlatformAngleD3DExtension}). Client extensions: " +
                $"{clientExtensions ?? "(eglQueryString returned nothing)"}.");
        }

        return CreateOnAngleDevice(trace);
    }

    /// <summary>
    /// Reads the client extension string, tolerating a library that cannot.
    /// </summary>
    /// <remarks>
    /// A build predating <c>EGL_EXT_client_extensions</c> returns null here
    /// rather than failing. That is a legitimate answer — it means no extension
    /// is safe to assume — so it must not be turned into a crash of its own.
    /// </remarks>
    private static string? ReadClientExtensions(Action<string> step)
    {
        try
        {
            string? extensions = Egl.QueryString(Egl.EGL_NO_DISPLAY, Egl.EGL_EXTENSIONS);
            step($"EGL client extensions: {extensions ?? "(none reported)"}");
            return extensions;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // Rethrown as the pipeline's own type so the window reports "ANGLE is
            // missing" rather than a bare loader message with no context.
            throw new RenderInitializationException(
                "libEGL.dll could not be loaded or does not export eglQueryString. " +
                "The ANGLE runtime beside the executable is missing or is not a " +
                "64-bit build.",
                error);
        }
    }

    /// <summary>
    /// Path one: the app creates the Direct3D device and ANGLE adopts it.
    /// </summary>
    private static AngleContext CreateOnAppDevice(Action<string>? trace)
    {
        void Step(string message) => trace?.Invoke(message);

        ID3D11Device device = CreateHardwareDevice();
        Step($"D3D11 device created by the app, feature level {device.FeatureLevel}");

        nint eglDevice = nint.Zero;
        nint display = Egl.EGL_NO_DISPLAY;
        try
        {
            // There is no display attribute that accepts a raw ID3D11Device*. An
            // earlier version of this code put EGL_D3D11_DEVICE_ANGLE into the
            // attribute list, which is a device-creation token, not a display
            // attribute — at best ANGLE ignored it and silently created a second
            // device of its own, which breaks the single-device invariant the
            // whole pipeline rests on (see docs/RENDERING.md §2).
            eglDevice = Egl.CreateDeviceAngle(Egl.EGL_D3D11_DEVICE_ANGLE, device.NativePointer, null);
            Step($"eglCreateDeviceANGLE -> 0x{eglDevice:X}");

            if (eglDevice == nint.Zero)
            {
                throw new RenderInitializationException(
                    $"eglCreateDeviceANGLE failed: {Egl.DescribeLastError()}.");
            }

            // The attribute list here is EGLint-sized; see the declaration of
            // GetPlatformDisplay.
            display = Egl.GetPlatformDisplay(Egl.EGL_PLATFORM_DEVICE_EXT, eglDevice, null);
            Step($"eglGetPlatformDisplayEXT(EGL_PLATFORM_DEVICE_EXT) -> 0x{display:X}");

            if (display == Egl.EGL_NO_DISPLAY)
            {
                throw new RenderInitializationException(
                    "eglGetPlatformDisplayEXT would not build a display on the app's " +
                    $"D3D11 device: {Egl.DescribeLastError()}.");
            }

            InitializeDisplay(display, Step);
            return Finish(display, eglDevice, ownsEglDevice: true, device, Step);
        }
        catch
        {
            if (display != Egl.EGL_NO_DISPLAY)
            {
                _ = Egl.Terminate(display);
            }

            if (eglDevice != nint.Zero)
            {
                _ = Egl.ReleaseDeviceAngle(eglDevice);
            }

            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Path two: ANGLE creates the Direct3D device and the app adopts it.
    /// </summary>
    /// <remarks>
    /// This is ANGLE's ordinary mode of operation, so it works on every build
    /// that has the D3D11 backend at all. The device is then read back out of
    /// the display, which is what keeps the pipeline on a single device even
    /// though the app did not create it.
    /// </remarks>
    private static AngleContext CreateOnAngleDevice(Action<string>? trace)
    {
        void Step(string message) => trace?.Invoke(message);

        int* displayAttributes = stackalloc int[]
        {
            Egl.EGL_PLATFORM_ANGLE_TYPE_ANGLE, Egl.EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE,
            Egl.EGL_NONE,
        };

        nint display = Egl.GetPlatformDisplay(
            Egl.EGL_PLATFORM_ANGLE_ANGLE,
            Egl.EGL_DEFAULT_DISPLAY,
            displayAttributes);

        Step($"eglGetPlatformDisplayEXT(EGL_PLATFORM_ANGLE_ANGLE, D3D11) -> 0x{display:X}");

        if (display == Egl.EGL_NO_DISPLAY)
        {
            throw new RenderInitializationException(
                "eglGetPlatformDisplayEXT would not build a Direct3D 11 display: " +
                $"{Egl.DescribeLastError()}.");
        }

        ID3D11Device? device = null;
        try
        {
            InitializeDisplay(display, Step);

            // Display extensions, not client extensions: the query entry points
            // are advertised per display, and calling them on a display that does
            // not advertise them is the same abort risk as before.
            string? displayExtensions = Egl.QueryString(display, Egl.EGL_EXTENSIONS);
            if (!Egl.HasExtension(displayExtensions, DeviceQueryExtension))
            {
                throw new RenderInitializationException(
                    $"The ANGLE display does not advertise {DeviceQueryExtension}, so the " +
                    "Direct3D device behind it cannot be read out. Display extensions: " +
                    $"{displayExtensions ?? "(none reported)"}.");
            }

            nint eglDevice;
            if (Egl.QueryDisplayAttrib(display, Egl.EGL_DEVICE_EXT, &eglDevice) == Egl.EGL_FALSE)
            {
                throw new RenderInitializationException(
                    $"eglQueryDisplayAttribEXT(EGL_DEVICE_EXT) failed: {Egl.DescribeLastError()}.");
            }

            Step($"eglQueryDisplayAttribEXT(EGL_DEVICE_EXT) -> 0x{eglDevice:X}");

            nint devicePointer;
            if (Egl.QueryDeviceAttrib(eglDevice, Egl.EGL_D3D11_DEVICE_ANGLE, &devicePointer) == Egl.EGL_FALSE
                || devicePointer == nint.Zero)
            {
                throw new RenderInitializationException(
                    "eglQueryDeviceAttribEXT(EGL_D3D11_DEVICE_ANGLE) failed: " +
                    $"{Egl.DescribeLastError()}. The display is not on the D3D11 backend.");
            }

            // ANGLE keeps its own reference; this adds ours, so that disposing
            // the wrapper below releases exactly what it took and no more.
            _ = Marshal.AddRef(devicePointer);
            device = new ID3D11Device(devicePointer);

            Step($"adopted ANGLE's D3D11 device, feature level {device.FeatureLevel}");

            // ANGLE owns this EGLDeviceEXT: releasing it would free a device the
            // display is still using.
            return Finish(display, eglDevice, ownsEglDevice: false, device, Step);
        }
        catch
        {
            device?.Dispose();
            _ = Egl.Terminate(display);
            throw;
        }
    }

    private static void InitializeDisplay(nint display, Action<string> step)
    {
        int major = 0;
        int minor = 0;
        if (Egl.Initialize(display, &major, &minor) == Egl.EGL_FALSE)
        {
            throw new RenderInitializationException(
                $"eglInitialize failed: {Egl.DescribeLastError()}.");
        }

        step($"eglInitialize -> EGL {major}.{minor}, " +
             $"{Egl.QueryString(display, Egl.EGL_VENDOR) ?? "unknown vendor"}");
    }

    /// <summary>
    /// The part both paths share: pick a config, make a context, protect the device.
    /// </summary>
    private static AngleContext Finish(
        nint display,
        nint eglDevice,
        bool ownsEglDevice,
        ID3D11Device device,
        Action<string> step)
    {
        int* configAttributes = stackalloc int[]
        {
            Egl.EGL_RED_SIZE, 8,
            Egl.EGL_GREEN_SIZE, 8,
            Egl.EGL_BLUE_SIZE, 8,
            Egl.EGL_ALPHA_SIZE, 8,

            // Depth and stencil are requested because libmpv's shader passes may
            // use them; a config without them makes render context creation fail
            // in a way that reports only "unsupported".
            Egl.EGL_DEPTH_SIZE, 0,
            Egl.EGL_STENCIL_SIZE, 0,

            Egl.EGL_SURFACE_TYPE, Egl.EGL_PBUFFER_BIT,
            Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES3_BIT,
            Egl.EGL_NONE,
        };

        nint config;
        int configCount = 0;
        if (Egl.ChooseConfig(display, configAttributes, &config, 1, &configCount) == Egl.EGL_FALSE
            || configCount == 0)
        {
            throw new RenderInitializationException(
                $"eglChooseConfig found no ES3 pbuffer config: {Egl.DescribeLastError()}.");
        }

        step($"eglChooseConfig -> {configCount} config(s)");

        int* contextAttributes = stackalloc int[]
        {
            Egl.EGL_CONTEXT_CLIENT_VERSION, 3,
            Egl.EGL_NONE,
        };

        nint context = Egl.CreateContext(display, config, Egl.EGL_NO_CONTEXT, contextAttributes);
        if (context == Egl.EGL_NO_CONTEXT)
        {
            throw new RenderInitializationException(
                $"eglCreateContext failed for OpenGL ES 3: {Egl.DescribeLastError()}.");
        }

        // Direct3D serialises calls from multiple threads only when told to, and
        // this device is touched by the UI thread, the render thread and ANGLE's
        // own internals. Set on both paths: ANGLE enables it for its own use, but
        // that is its choice to change, not a guarantee to build on.
        using (ID3D11Multithread multithread = device.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true);
        }

        step("ANGLE context created");
        return new AngleContext(display, config, context, eglDevice, ownsEglDevice, device);
    }

    /// <summary>Creates a hardware Direct3D 11 device for the app to own.</summary>
    private static ID3D11Device CreateHardwareDevice()
    {
        // BgraSupport is required by the composition swap chain; VideoSupport is
        // required by d3d11va, which is the decoder the whole zero-copy path
        // depends on.
        const DeviceCreationFlags Flags =
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

        FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
        ];

        var result = D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            Flags,
            featureLevels,
            out ID3D11Device? device,
            out _,
            out ID3D11DeviceContext? context);

        if (result.Failure || device is null)
        {
            throw new RenderInitializationException(
                $"D3D11CreateDevice failed with {result}. The machine has no Direct3D 11 " +
                "capable adapter, or the adapter is in a removed state.");
        }

        // The immediate context is fetched from the device where it is needed;
        // holding this second reference would leak it.
        context?.Dispose();
        return device;
    }

    /// <summary>Makes this context current on the calling thread.</summary>
    internal void MakeCurrent(nint surface)
    {
        if (Egl.MakeCurrent(_display, surface, surface, _context) == Egl.EGL_FALSE)
        {
            throw new RenderInitializationException(
                $"eglMakeCurrent failed: {Egl.DescribeLastError()}.");
        }
    }

    /// <summary>Detaches any current context from the calling thread.</summary>
    /// <remarks>
    /// The result is discarded deliberately. This runs on teardown paths where
    /// there is nothing useful to do about a failure, and where throwing would
    /// mask the error that started the teardown.
    /// </remarks>
    internal void ClearCurrent() =>
        _ = Egl.MakeCurrent(_display, Egl.EGL_NO_SURFACE, Egl.EGL_NO_SURFACE, Egl.EGL_NO_CONTEXT);

    /// <summary>
    /// Resolves a GL entry point for libmpv.
    /// </summary>
    /// <remarks>
    /// libmpv calls this whenever it compiles a shader, not only during setup,
    /// so the delegate that reaches here must stay alive for the life of the
    /// render context.
    /// </remarks>
    internal static nint ResolveProcAddress(string name)
    {
        int byteCount = Encoding.ASCII.GetByteCount(name);
        byte* buffer = stackalloc byte[byteCount + 1];
        fixed (char* source = name)
        {
            Encoding.ASCII.GetBytes(source, name.Length, buffer, byteCount);
        }

        buffer[byteCount] = 0;
        return Egl.GetProcAddress(buffer);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_display != Egl.EGL_NO_DISPLAY)
        {
            ClearCurrent();

            if (_context != Egl.EGL_NO_CONTEXT)
            {
                _ = Egl.DestroyContext(_display, _context);
                _context = Egl.EGL_NO_CONTEXT;
            }

            _ = Egl.Terminate(_display);
            _display = Egl.EGL_NO_DISPLAY;
            _config = nint.Zero;
        }

        // After the display, never before: terminating a display whose device has
        // already been released is undefined. Skipped entirely when ANGLE created
        // the device, because then this handle is not ours to free.
        if (_eglDevice != nint.Zero)
        {
            if (_ownsEglDevice)
            {
                _ = Egl.ReleaseDeviceAngle(_eglDevice);
            }

            _eglDevice = nint.Zero;
        }

        // Last, because everything above may still be using it. On the adoption
        // path this releases the reference taken in CreateOnAngleDevice and
        // leaves ANGLE's own intact.
        _device?.Dispose();
        _device = null;
    }
}

/// <summary>The video pipeline could not be built.</summary>
public sealed class RenderInitializationException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public RenderInitializationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public RenderInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with a default message.</summary>
    public RenderInitializationException()
        : base("The video pipeline could not be initialized.")
    {
    }
}
