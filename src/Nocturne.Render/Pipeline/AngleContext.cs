using System.Text;
using Nocturne.Render.Interop;
using Vortice.Direct3D11;

namespace Nocturne.Render.Pipeline;

/// <summary>
/// An OpenGL ES context that renders straight into Direct3D 11 textures.
/// </summary>
/// <remarks>
/// Built on the app's own <see cref="ID3D11Device"/> rather than one ANGLE
/// creates for itself. Sharing the device is what keeps the frame path free of
/// cross-device synchronisation: with two devices, every frame would need a
/// shared handle and a keyed mutex, and that wait lands inside the presentation
/// interval where it is most visible.
/// </remarks>
internal sealed unsafe class AngleContext : IDisposable
{
    private nint _display;
    private nint _config;
    private nint _context;
    private bool _disposed;

    private AngleContext(nint display, nint config, nint context)
    {
        _display = display;
        _config = config;
        _context = context;
    }

    /// <summary>The EGL display, needed when creating surfaces.</summary>
    internal nint Display => _display;

    /// <summary>The chosen EGL config, needed when wrapping a texture.</summary>
    internal nint Config => _config;

    /// <summary>
    /// Creates a context bound to <paramref name="device"/>.
    /// </summary>
    /// <exception cref="RenderInitializationException">ANGLE could not be set up.</exception>
    internal static AngleContext Create(ID3D11Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        int* displayAttributes = stackalloc int[]
        {
            Egl.EGL_PLATFORM_ANGLE_TYPE_ANGLE, Egl.EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE,
            Egl.EGL_PLATFORM_ANGLE_D3D11_DEVICE_ANGLE, (int)device.NativePointer,
            Egl.EGL_NONE,
        };

        // The attribute array carries a pointer in an int slot on purpose: the
        // EGL attribute type is intptr-sized (EGLAttrib) for this entry point,
        // so the value must be written as a native-sized word, not truncated.
        nint* wideAttributes = stackalloc nint[]
        {
            Egl.EGL_PLATFORM_ANGLE_TYPE_ANGLE, Egl.EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE,
            Egl.EGL_PLATFORM_ANGLE_D3D11_DEVICE_ANGLE, device.NativePointer,
            Egl.EGL_NONE,
        };
        _ = displayAttributes;

        nint display = Egl.GetPlatformDisplay(
            Egl.EGL_PLATFORM_ANGLE_ANGLE,
            Egl.EGL_DEFAULT_DISPLAY,
            (int*)wideAttributes);

        if (display == Egl.EGL_NO_DISPLAY)
        {
            throw new RenderInitializationException(
                "eglGetPlatformDisplayEXT failed. ANGLE's libEGL.dll is present but would not " +
                $"build a display on the app's D3D11 device: {Egl.DescribeLastError()}.");
        }

        int major = 0;
        int minor = 0;
        if (Egl.Initialize(display, &major, &minor) == Egl.EGL_FALSE)
        {
            throw new RenderInitializationException(
                $"eglInitialize failed: {Egl.DescribeLastError()}.");
        }

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
            _ = Egl.Terminate(display);
            throw new RenderInitializationException(
                $"eglChooseConfig found no ES3 pbuffer config: {Egl.DescribeLastError()}.");
        }

        int* contextAttributes = stackalloc int[]
        {
            Egl.EGL_CONTEXT_CLIENT_VERSION, 3,
            Egl.EGL_NONE,
        };

        nint context = Egl.CreateContext(display, config, Egl.EGL_NO_CONTEXT, contextAttributes);
        if (context == Egl.EGL_NO_CONTEXT)
        {
            _ = Egl.Terminate(display);
            throw new RenderInitializationException(
                $"eglCreateContext failed for OpenGL ES 3: {Egl.DescribeLastError()}.");
        }

        return new AngleContext(display, config, context);
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

        if (_display == Egl.EGL_NO_DISPLAY)
        {
            return;
        }

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
