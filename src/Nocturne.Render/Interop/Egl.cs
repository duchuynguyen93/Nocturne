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
    /// Hands ANGLE a device the app created rather than letting it create one.
    /// </summary>
    /// <remarks>
    /// Sharing one device is what removes cross-device synchronisation from the
    /// per-frame path. With two devices every frame needs a shared handle and a
    /// keyed mutex, and the fence wait lands squarely in the presentation
    /// interval.
    /// </remarks>
    internal const int EGL_PLATFORM_ANGLE_D3D11_DEVICE_ANGLE = 0x33A1;

    /// <summary>Client buffer type naming a D3D11 texture.</summary>
    internal const int EGL_D3D_TEXTURE_ANGLE = 0x33A3;

    internal const int GL_FRAMEBUFFER_BINDING = 0x8CA6;
    internal const int GL_RGBA8 = 0x8058;
    internal const int GL_RGB10_A2 = 0x8059;

    [DllImport(EglLibrary, EntryPoint = "eglGetError")]
    internal static extern int GetError();

    [DllImport(EglLibrary, EntryPoint = "eglGetProcAddress")]
    internal static extern nint GetProcAddress(byte* procName);

    [DllImport(EglLibrary, EntryPoint = "eglGetPlatformDisplayEXT")]
    internal static extern nint GetPlatformDisplay(int platform, nint nativeDisplay, int* attributes);

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
