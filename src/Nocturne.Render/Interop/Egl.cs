using System.Runtime.InteropServices;

namespace Nocturne.Render.Interop;

/// <summary>
/// The subset of EGL and GLES the video pipeline uses, as provided by ANGLE.
/// </summary>
/// <remarks>
/// <para>
/// ANGLE is the bridge between libmpv's OpenGL render API and Direct3D 11. The
/// alternative — a real desktop GL driver — would mean the decoded D3D11
/// textures produced by <c>d3d11va</c> could not be sampled without a readback
/// through system memory, which is the single change that turns idle 4K playback
/// from roughly free into steady CPU load.
/// </para>
/// <para>
/// The extensions this code depends on are <c>EGL_ANGLE_platform_angle_d3d</c>
/// (to build a display on a device the app already owns) and
/// <c>EGL_ANGLE_d3d_texture_client_buffer</c> (to make a texture the app already
/// owns into a render target). Both ship in the ANGLE binaries that accompany
/// the official libmpv Windows builds.
/// </para>
/// </remarks>
internal static unsafe class Egl
{
    private const string EglLibrary = "libEGL.dll";
    private const string GlesLibrary = "libGLESv2.dll";

    internal const nint EGL_NO_DISPLAY = 0;
    internal const nint EGL_NO_CONTEXT = 0;
    internal const nint EGL_NO_SURFACE = 0;
    internal const nint EGL_DEFAULT_DISPLAY = 0;

    internal const int EGL_FALSE = 0;
    internal const int EGL_TRUE = 1;
    internal const int EGL_NONE = 0x3038;

    internal const int EGL_SUCCESS = 0x3000;

    internal const int EGL_VENDOR = 0x3053;
    internal const int EGL_VERSION = 0x3054;
    internal const int EGL_EXTENSIONS = 0x3055;

    internal const int EGL_ALPHA_SIZE = 0x3021;
    internal const int EGL_BLUE_SIZE = 0x3022;
    internal const int EGL_GREEN_SIZE = 0x3023;
    internal const int EGL_RED_SIZE = 0x3024;
    internal const int EGL_DEPTH_SIZE = 0x3025;
    internal const int EGL_STENCIL_SIZE = 0x3026;
    internal const int EGL_SURFACE_TYPE = 0x3033;
    internal const int EGL_RENDERABLE_TYPE = 0x3040;
    internal const int EGL_PBUFFER_BIT = 0x0001;
    internal const int EGL_OPENGL_ES2_BIT = 0x0004;
    internal const int EGL_OPENGL_ES3_BIT = 0x0040;
    internal const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;

    internal const int EGL_WIDTH = 0x3057;
    internal const int EGL_HEIGHT = 0x3056;
    internal const int EGL_TEXTURE_FORMAT = 0x3080;
    internal const int EGL_TEXTURE_RGBA = 0x305E;
    internal const int EGL_TEXTURE_TARGET = 0x3081;
    internal const int EGL_TEXTURE_2D = 0x305F;

    /// <summary>Platform selector for <c>eglGetPlatformDisplayEXT</c>.</summary>
    internal const int EGL_PLATFORM_ANGLE_ANGLE = 0x3202;

    /// <summary>Requests the Direct3D 11 backend.</summary>
    internal const int EGL_PLATFORM_ANGLE_TYPE_ANGLE = 0x3203;
    internal const int EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE = 0x3208;

    /// <summary>
    /// Platform selector naming an <c>EGLDeviceEXT</c> as the native display.
    /// </summary>
    /// <remarks>
    /// This — not an attribute in the attribute list — is how ANGLE is given a
    /// device the app already owns. The device is wrapped with
    /// <c>eglCreateDeviceANGLE</c> first, then passed here as the native
    /// display. Sharing one device is what removes cross-device synchronisation
    /// from the per-frame path: with two devices every frame needs a shared
    /// handle and a keyed mutex, and that wait lands squarely inside the
    /// presentation interval.
    /// </remarks>
    internal const int EGL_PLATFORM_DEVICE_EXT = 0x313F;

    /// <summary>
    /// Device type naming a D3D11 device, for <c>eglCreateDeviceANGLE</c>.
    /// </summary>
    /// <remarks>
    /// From <c>EGL_ANGLE_device_d3d</c>. It is a device-creation token and a
    /// queryable device attribute — it is <em>not</em> a display attribute, and
    /// passing it in a display attribute list does not hand ANGLE the device.
    /// </remarks>
    internal const int EGL_D3D11_DEVICE_ANGLE = 0x33A1;

    /// <summary>Client buffer type naming a D3D11 texture.</summary>
    internal const int EGL_D3D_TEXTURE_ANGLE = 0x33A3;

    /// <summary>Queries the <c>EGLDeviceEXT</c> behind a display.</summary>
    /// <remarks>
    /// From <c>EGL_EXT_device_query</c>. This is the route used when ANGLE
    /// creates the Direct3D device rather than adopting one of ours: ask the
    /// display which device it built itself on, then ask that device for the
    /// underlying <c>ID3D11Device</c>.
    /// </remarks>
    internal const int EGL_DEVICE_EXT = 0x322C;

    internal const int GL_FRAMEBUFFER_BINDING = 0x8CA6;
    internal const int GL_RGBA8 = 0x8058;
    internal const int GL_RGB10_A2 = 0x8059;

    [DllImport(EglLibrary, EntryPoint = "eglGetError")]
    internal static extern int GetError();

    [DllImport(EglLibrary, EntryPoint = "eglGetProcAddress")]
    internal static extern nint GetProcAddress(byte* procName);

    /// <summary>
    /// Reads one of EGL's descriptive strings.
    /// </summary>
    /// <remarks>
    /// Called with <see cref="EGL_NO_DISPLAY"/> it returns the <em>client</em>
    /// extension string — the set of entry points usable before any display
    /// exists. That query is the only supported way to find out whether
    /// <c>eglCreateDeviceANGLE</c> may be called at all, and calling an ANGLE
    /// extension the build does not implement is not a graceful failure: it
    /// reaches an <c>UNREACHABLE()</c> and aborts the process.
    /// </remarks>
    [DllImport(EglLibrary, EntryPoint = "eglQueryString")]
    internal static extern byte* QueryStringRaw(nint display, int name);

    /// <summary>Reads an attribute of an initialized display.</summary>
    /// <remarks>From <c>EGL_EXT_device_query</c>.</remarks>
    [DllImport(EglLibrary, EntryPoint = "eglQueryDisplayAttribEXT")]
    internal static extern int QueryDisplayAttrib(nint display, int attribute, nint* value);

    /// <summary>Reads an attribute of an <c>EGLDeviceEXT</c>.</summary>
    /// <remarks>From <c>EGL_EXT_device_query</c>.</remarks>
    [DllImport(EglLibrary, EntryPoint = "eglQueryDeviceAttribEXT")]
    internal static extern int QueryDeviceAttrib(nint device, int attribute, nint* value);

    /// <summary>
    /// Builds a display for a platform-specific native display handle.
    /// </summary>
    /// <remarks>
    /// The attribute list is <c>EGLint*</c> — 32-bit entries, even on x64. Only
    /// the EGL 1.5 core <c>eglGetPlatformDisplay</c> takes pointer-sized
    /// <c>EGLAttrib</c>. Writing a 64-bit array and casting it here would make
    /// ANGLE read each pointer as two separate attributes.
    /// </remarks>
    [DllImport(EglLibrary, EntryPoint = "eglGetPlatformDisplayEXT")]
    internal static extern nint GetPlatformDisplay(int platform, nint nativeDisplay, int* attributes);

    /// <summary>
    /// Wraps a Direct3D device as an <c>EGLDeviceEXT</c>.
    /// </summary>
    /// <remarks>
    /// From <c>EGL_ANGLE_device_creation_d3d11</c>. This is the supported route
    /// for handing ANGLE a device the app created; the resulting device is then
    /// passed to <see cref="GetPlatformDisplay"/> under
    /// <see cref="EGL_PLATFORM_DEVICE_EXT"/>.
    /// </remarks>
    /// <remarks>
    /// The attribute list is <c>EGLAttrib*</c> — pointer-sized, unlike the
    /// <c>EGLint*</c> that <see cref="GetPlatformDisplay"/> takes. Only
    /// <see langword="null"/> is passed today; declaring it as <c>int*</c>
    /// would leave the next person to add a real attribute with entries read
    /// four bytes apart on x64.
    /// </remarks>
    [DllImport(EglLibrary, EntryPoint = "eglCreateDeviceANGLE")]
    internal static extern nint CreateDeviceAngle(int deviceType, nint device, nint* attributes);

    [DllImport(EglLibrary, EntryPoint = "eglReleaseDeviceANGLE")]
    internal static extern int ReleaseDeviceAngle(nint device);

    [DllImport(EglLibrary, EntryPoint = "eglInitialize")]
    internal static extern int Initialize(nint display, int* major, int* minor);

    [DllImport(EglLibrary, EntryPoint = "eglTerminate")]
    internal static extern int Terminate(nint display);

    [DllImport(EglLibrary, EntryPoint = "eglChooseConfig")]
    internal static extern int ChooseConfig(
        nint display,
        int* attributes,
        nint* configs,
        int configSize,
        int* configCount);

    [DllImport(EglLibrary, EntryPoint = "eglCreateContext")]
    internal static extern nint CreateContext(nint display, nint config, nint shareContext, int* attributes);

    [DllImport(EglLibrary, EntryPoint = "eglDestroyContext")]
    internal static extern int DestroyContext(nint display, nint context);

    /// <summary>
    /// Wraps a D3D11 texture the app owns as an EGL surface ANGLE can draw into.
    /// </summary>
    /// <remarks>
    /// This is the whole trick. libmpv renders through GL into this surface, and
    /// the pixels land in a Direct3D texture that DXGI can present — with no
    /// copy through system memory anywhere in the path.
    /// </remarks>
    [DllImport(EglLibrary, EntryPoint = "eglCreatePbufferFromClientBuffer")]
    internal static extern nint CreatePbufferFromClientBuffer(
        nint display,
        int bufferType,
        nint buffer,
        nint config,
        int* attributes);

    [DllImport(EglLibrary, EntryPoint = "eglDestroySurface")]
    internal static extern int DestroySurface(nint display, nint surface);

    [DllImport(EglLibrary, EntryPoint = "eglMakeCurrent")]
    internal static extern int MakeCurrent(nint display, nint draw, nint read, nint context);

    [DllImport(GlesLibrary, EntryPoint = "glFlush")]
    internal static extern void Flush();

    [DllImport(GlesLibrary, EntryPoint = "glGetIntegerv")]
    internal static extern void GetIntegerV(int name, int* values);

    /// <summary>
    /// Reads an EGL string as managed text, or null when EGL returned nothing.
    /// </summary>
    internal static string? QueryString(nint display, int name)
    {
        byte* raw = QueryStringRaw(display, name);
        return raw is null ? null : Marshal.PtrToStringAnsi((nint)raw);
    }

    /// <summary>
    /// Tests whether a space-separated EGL extension list contains a name.
    /// </summary>
    /// <remarks>
    /// Substring matching would be wrong: <c>EGL_ANGLE_device_creation</c> is a
    /// prefix of <c>EGL_ANGLE_device_creation_d3d11</c>, and the two guard
    /// different entry points.
    /// </remarks>
    internal static bool HasExtension(string? extensions, string name)
    {
        if (string.IsNullOrEmpty(extensions))
        {
            return false;
        }

        // Hand-walked rather than String.Split: this runs on the startup path
        // and the list ANGLE returns is long, so there is no reason to allocate
        // an array of forty strings to answer one yes-or-no question.
        int start = 0;
        while (start < extensions.Length)
        {
            int end = extensions.IndexOf(' ', start);
            if (end < 0)
            {
                end = extensions.Length;
            }

            if (extensions.AsSpan(start, end - start).SequenceEqual(name))
            {
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    /// <summary>
    /// Turns the last EGL error into a message worth reading.
    /// </summary>
    /// <remarks>
    /// EGL reports failure by returning a null handle and stashing a code, so
    /// without this every setup failure reads as "something returned null".
    /// </remarks>
    internal static string DescribeLastError()
    {
        int error = GetError();
        string name = error switch
        {
            EGL_SUCCESS => "EGL_SUCCESS",
            0x3001 => "EGL_NOT_INITIALIZED",
            0x3002 => "EGL_BAD_ACCESS",
            0x3003 => "EGL_BAD_ALLOC",
            0x3004 => "EGL_BAD_ATTRIBUTE",
            0x3005 => "EGL_BAD_CONFIG",
            0x3006 => "EGL_BAD_CONTEXT",
            0x3007 => "EGL_BAD_CURRENT_SURFACE",
            0x3008 => "EGL_BAD_DISPLAY",
            0x3009 => "EGL_BAD_MATCH",
            0x300A => "EGL_BAD_NATIVE_PIXMAP",
            0x300B => "EGL_BAD_NATIVE_WINDOW",
            0x300C => "EGL_BAD_PARAMETER",
            0x300D => "EGL_BAD_SURFACE",
            0x300E => "EGL_CONTEXT_LOST",
            _ => "unknown",
        };

        return $"{name} (0x{error:X4})";
    }
}
