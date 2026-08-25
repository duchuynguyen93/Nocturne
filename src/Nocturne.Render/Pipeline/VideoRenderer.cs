using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nocturne.Engine.Interop;
using Nocturne.Render.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Nocturne.Render.Pipeline;

/// <summary>
/// Drives libmpv's renderer into a composition swap chain.
/// </summary>
/// <remarks>
/// <para>
/// The per-frame path is: libmpv draws through ANGLE into a Direct3D texture the
/// app owns, that texture is copied into the swap chain's back buffer, the swap
/// chain is presented, and libmpv is told when the frame landed. No pixel
/// crosses the PCIe bus into system memory at any point, which is what keeps
/// idle 4K playback near zero CPU.
/// </para>
/// <para>
/// The swap chain is created for composition rather than for a window. That is
/// what lets the app's XAML compose over it — translucent transport bar, rounded
/// corners, animations — instead of the video sitting in a child HWND that
/// covers everything drawn near it.
/// </para>
/// <para>
/// Rendering runs on its own thread. libmpv signals a frame through an
/// unmanaged callback that must do nothing but set an event: it is invoked from
/// inside libmpv's own locks, and calling back into libmpv from it deadlocks.
/// </para>
/// </remarks>
public sealed unsafe class VideoRenderer : IDisposable
{
    /// <summary>Back buffer count. Two is enough for flip-model presentation.</summary>
    private const int BufferCount = 2;

    private static readonly Dictionary<nint, VideoRenderer> Instances = [];
    private static readonly object InstanceLock = new();
    private static nint _nextInstanceKey = 1;

    private readonly nint _instanceKey;
    private readonly AutoResetEvent _frameAvailable = new(initialState: false);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _resizeLock = new();
    private readonly Thread _renderThread;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _deviceContext = null!;
    private IDXGISwapChain1 _swapChain = null!;
    private ID3D11Texture2D? _renderTexture;
    private AngleContext _angle = null!;

    private nint _eglSurface = Egl.EGL_NO_SURFACE;
    private nint _renderContext;

    private int _pendingWidth;
    private int _pendingHeight;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private volatile bool _resizeRequested;
    private bool _disposed;

    private VideoRenderer(nint instanceKey)
    {
        _instanceKey = instanceKey;
        _renderThread = new Thread(RenderLoop)
        {
            Name = "Nocturne video render",
            IsBackground = true,

            // Above normal, below time-critical. Frame pacing suffers visibly
            // when the render thread loses to background work, and running at
            // Highest starves the UI thread that feeds it.
            Priority = ThreadPriority.AboveNormal,
        };
    }

    /// <summary>Raised when the render thread fails and playback has stopped.</summary>
    /// <remarks>Raised on the render thread.</remarks>
    public event EventHandler<Exception>? RenderFailed;

    /// <summary>
    /// The swap chain to hand to <c>ISwapChainPanelNative.SetSwapChain</c>.
    /// </summary>
    public IDXGISwapChain1 SwapChain => _swapChain;

    /// <summary>
    /// Builds the pipeline and attaches it to a libmpv handle.
    /// </summary>
    /// <param name="mpvHandle">Handle from an initialized <c>PlayerEngine</c>.</param>
    /// <param name="width">Initial surface width in physical pixels.</param>
    /// <param name="height">Initial surface height in physical pixels.</param>
    /// <exception cref="RenderInitializationException">Any stage failed.</exception>
    public static VideoRenderer Create(nint mpvHandle, int width, int height)
    {
        if (mpvHandle == nint.Zero)
        {
            throw new ArgumentException("The libmpv handle is null.", nameof(mpvHandle));
        }

        nint key;
        lock (InstanceLock)
        {
            key = _nextInstanceKey++;
        }

        var renderer = new VideoRenderer(key);
        try
        {
            renderer.Initialize(mpvHandle, Math.Max(1, width), Math.Max(1, height));
        }
        catch
        {
            renderer.Dispose();
            throw;
        }

        lock (InstanceLock)
        {
            Instances[key] = renderer;
        }

        renderer._renderThread.Start();
        return renderer;
    }

    /// <summary>
    /// Records a new surface size, applied on the next frame.
    /// </summary>
    /// <remarks>
    /// Resizing is deferred to the render thread rather than done inline. The
    /// caller is the UI thread reacting to a layout pass, and tearing down the
    /// swap chain's buffers underneath a render in progress is a device-removed
    /// error, not a resize.
    /// </remarks>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        lock (_resizeLock)
        {
            if (_pendingWidth == width && _pendingHeight == height)
            {
                return;
            }

            _pendingWidth = width;
            _pendingHeight = height;
            _resizeRequested = true;
        }

        // Wake the loop so a paused video still repaints at the new size instead
        // of showing a stretched last frame until playback resumes.
        _frameAvailable.Set();
    }

    private void Initialize(nint mpvHandle, int width, int height)
    {
        _pendingWidth = width;
        _pendingHeight = height;
        _surfaceWidth = width;
        _surfaceHeight = height;

        CreateDevice();
        CreateSwapChain(width, height);

        _angle = AngleContext.Create(_device);
        CreateRenderSurface(width, height);

        // The context must be current on the thread that creates the mpv render
        // context, and on every thread that renders. The render thread makes it
        // current again as its first act.
        _angle.MakeCurrent(_eglSurface);
        CreateMpvRenderContext(mpvHandle);
        _angle.ClearCurrent();
    }

    private void CreateDevice()
    {
        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
#if DEBUG
        // The debug layer is what turns a silent black frame into a named
        // reason. It is absent on machines without the Graphics Tools feature,
        // so a failure here retries without it rather than refusing to start.
        flags |= DeviceCreationFlags.Debug;
#endif

        FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
        ];

        var result = D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            flags,
            featureLevels,
            out ID3D11Device? device,
            out _,
            out ID3D11DeviceContext? context);

        if (result.Failure)
        {
#if DEBUG
            result = D3D11.D3D11CreateDevice(
                adapter: null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                featureLevels,
                out device,
                out _,
                out context);
#endif
            if (result.Failure)
            {
                throw new RenderInitializationException(
                    $"D3D11CreateDevice failed with {result}. The machine has no Direct3D 11 " +
                    "capable adapter, or the adapter is in a removed state.");
            }
        }

        _device = device!;
        _deviceContext = context!;

        // Direct3D serialises calls from multiple threads only when told to. The
        // UI thread and the render thread both touch this device.
        using ID3D11Multithread multithread = _device.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
    }

    private void CreateSwapChain(int width, int height)
    {
        using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,

            // B8G8R8A8 rather than R8G8B8A8: it is the format the composition
            // engine consumes without a conversion pass. The HDR path replaces
            // this with R10G10B10A2 once the swap chain colour space is set;
            // see docs/RENDERING.md.
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,

            // Composition swap chains must use Stretch scaling and a premultiplied
            // alpha mode; anything else fails creation with E_INVALIDARG.
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
        };

        _swapChain = factory.CreateSwapChainForComposition(_device, description);

        // One frame of queued latency instead of the default three. Three frames
        // is right for a game that can run ahead; for video it is 50 ms of added
        // delay between a seek and the picture changing.
        using IDXGIDevice1 latencyDevice = _device.QueryInterface<IDXGIDevice1>();
        latencyDevice.MaximumFrameLatency = 1;
    }

    private void CreateRenderSurface(int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _renderTexture = _device.CreateTexture2D(description);

        int* surfaceAttributes = stackalloc int[]
        {
            Egl.EGL_WIDTH, width,
            Egl.EGL_HEIGHT, height,
            Egl.EGL_TEXTURE_FORMAT, Egl.EGL_TEXTURE_RGBA,
            Egl.EGL_TEXTURE_TARGET, Egl.EGL_TEXTURE_2D,
            Egl.EGL_NONE,
        };

        _eglSurface = Egl.CreatePbufferFromClientBuffer(
            _angle.Display,
            Egl.EGL_D3D_TEXTURE_ANGLE,
            _renderTexture.NativePointer,
            _angle.Config,
            surfaceAttributes);

        if (_eglSurface == Egl.EGL_NO_SURFACE)
        {
            throw new RenderInitializationException(
                "eglCreatePbufferFromClientBuffer refused the render texture: " +
                $"{Egl.DescribeLastError()}. This is the ANGLE build lacking " +
                "EGL_ANGLE_d3d_texture_client_buffer.");
        }

        _surfaceWidth = width;
        _surfaceHeight = height;
    }

    private void CreateMpvRenderContext(nint mpvHandle)
    {
        using Utf8RenderString apiType = new("opengl");

        var initParams = new MpvOpenGlInitParams
        {
            GetProcAddress = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, nint>)&GetProcAddressThunk,
            GetProcAddressContext = nint.Zero,
        };

        // ADVANCED_CONTROL is what enables the update callback and lets libmpv
        // schedule frames against real presentation times. Without it the render
        // API runs in a simpler mode that cannot do display-resample properly.
        int advancedControl = 1;

        MpvRenderParam* parameters = stackalloc MpvRenderParam[4];
        parameters[0] = new MpvRenderParam
        {
            Type = MpvRenderParamType.ApiType,
            Data = apiType.Pointer,
        };
        parameters[1] = new MpvRenderParam
        {
            Type = MpvRenderParamType.OpenGlInitParams,
            Data = (nint)(&initParams),
        };
        parameters[2] = new MpvRenderParam
        {
            Type = MpvRenderParamType.AdvancedControl,
            Data = (nint)(&advancedControl),
        };
        parameters[3] = new MpvRenderParam
        {
            Type = MpvRenderParamType.Invalid,
            Data = nint.Zero,
        };

        int result = MpvRenderNative.ContextCreate(out nint context, mpvHandle, parameters);
        if (result < 0)
        {
            throw new RenderInitializationException(
                $"mpv_render_context_create failed with {result}. The libmpv build does not " +
                "expose the OpenGL render API, or vo=libmpv was not set before initialization.");
        }

        _renderContext = context;

        MpvRenderNative.ContextSetUpdateCallback(
            _renderContext,
            &UpdateCallbackThunk,
            _instanceKey);
    }

    private void RenderLoop()
    {
        try
        {
            _angle.MakeCurrent(_eglSurface);

            while (!_cancellation.IsCancellationRequested)
            {
                // A timeout rather than an infinite wait: a resize while paused
                // must still repaint, and a lost update callback must not freeze
                // the surface until the next play.
                _frameAvailable.WaitOne(TimeSpan.FromMilliseconds(100));

                if (_cancellation.IsCancellationRequested)
                {
                    return;
                }

                if (_resizeRequested)
                {
                    ApplyPendingResize();
                }

                ulong flags = MpvRenderNative.ContextUpdate(_renderContext);
                if ((flags & MpvNativeConstants.UpdateFrameFlag) == 0)
                {
                    continue;
                }

                RenderFrame();
            }
        }
#pragma warning disable CA1031 // The render thread must report, not crash the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            RenderFailed?.Invoke(this, ex);
        }
        finally
        {
            _angle.ClearCurrent();
        }
    }

    private void RenderFrame()
    {
        var fbo = new MpvOpenGlFbo
        {
            // Zero means the framebuffer bound to the current surface, which is
            // the pbuffer wrapping the render texture.
            Fbo = 0,
            Width = _surfaceWidth,
            Height = _surfaceHeight,
            InternalFormat = Egl.GL_RGBA8,
        };

        // GL's origin is bottom-left and Direct3D's is top-left. Asking libmpv to
        // flip is cheaper than a second blit, and it is the flag to revisit first
        // if the picture appears upside down.
        int flipY = 1;

        MpvRenderParam* parameters = stackalloc MpvRenderParam[3];
        parameters[0] = new MpvRenderParam
        {
            Type = MpvRenderParamType.OpenGlFbo,
            Data = (nint)(&fbo),
        };
        parameters[1] = new MpvRenderParam
        {
            Type = MpvRenderParamType.FlipY,
            Data = (nint)(&flipY),
        };
        parameters[2] = new MpvRenderParam
        {
            Type = MpvRenderParamType.Invalid,
            Data = nint.Zero,
        };

        int result = MpvRenderNative.ContextRender(_renderContext, parameters);
        if (result < 0)
        {
            throw new RenderInitializationException(
                $"mpv_render_context_render failed with {result}.");
        }

        // Flush so ANGLE's commands reach the shared device before the copy
        // below reads the texture they wrote.
        Egl.Flush();

        using (ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
        {
            _deviceContext.CopyResource(backBuffer, _renderTexture!);
        }

        // Sync interval 1: present on the next vertical blank. libmpv's
        // display-resample timing is built on frames landing at that cadence.
        _swapChain.Present(1, PresentFlags.None);

        // libmpv derives its frame scheduling from these reports. Skipping them
        // is what turns 23.976 fps content into visible judder.
        MpvRenderNative.ContextReportSwap(_renderContext);
    }

    private void ApplyPendingResize()
    {
        int width;
        int height;
        lock (_resizeLock)
        {
            width = _pendingWidth;
            height = _pendingHeight;
            _resizeRequested = false;
        }

        if (width == _surfaceWidth && height == _surfaceHeight)
        {
            return;
        }

        // Order matters. The EGL surface holds a reference to the texture, so it
        // is released first; the swap chain buffers cannot be resized while a
        // back buffer reference is outstanding, so nothing may hold one here.
        _angle.ClearCurrent();

        if (_eglSurface != Egl.EGL_NO_SURFACE)
        {
            _ = Egl.DestroySurface(_angle.Display, _eglSurface);
            _eglSurface = Egl.EGL_NO_SURFACE;
        }

        _renderTexture?.Dispose();
        _renderTexture = null;

        _swapChain.ResizeBuffers(
            BufferCount,
            (uint)width,
            (uint)height,
            Format.B8G8R8A8_UNorm,
            SwapChainFlags.None);

        CreateRenderSurface(width, height);
        _angle.MakeCurrent(_eglSurface);
    }

    /// <summary>
    /// Called by libmpv when a frame is ready.
    /// </summary>
    /// <remarks>
    /// This runs inside libmpv's own locks. It may set an event and nothing
    /// else: calling back into libmpv from here deadlocks, and so does taking
    /// any lock the render thread might hold.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void UpdateCallbackThunk(nint callbackContext)
    {
        VideoRenderer? renderer;
        lock (InstanceLock)
        {
            Instances.TryGetValue(callbackContext, out renderer);
        }

        renderer?._frameAvailable.Set();
    }

    /// <summary>
    /// Resolves a GL entry point on libmpv's behalf.
    /// </summary>
    /// <remarks>
    /// Static and unmanaged-callers-only so there is no delegate for the GC to
    /// collect. A collected trampoline here crashes inside the GPU driver with
    /// no trace of its managed cause.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static nint GetProcAddressThunk(nint context, byte* name)
    {
        _ = context;
        return name is null ? nint.Zero : Egl.GetProcAddress(name);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (InstanceLock)
        {
            Instances.Remove(_instanceKey);
        }

        // Clear the callback before anything else: libmpv must not signal an
        // event that is about to be disposed.
        if (_renderContext != nint.Zero)
        {
            MpvRenderNative.ContextSetUpdateCallback(_renderContext, null, nint.Zero);
        }

        _cancellation.Cancel();
        _frameAvailable.Set();

        if (_renderThread.IsAlive && !_renderThread.Join(TimeSpan.FromSeconds(2)))
        {
            // Background thread; a timeout cannot keep the process alive, and
            // forcing teardown of GPU resources under it would be worse.
            RenderFailed?.Invoke(
                this,
                new TimeoutException("The video render thread did not stop within two seconds."));
        }

        if (_renderContext != nint.Zero)
        {
            MpvRenderNative.ContextFree(_renderContext);
            _renderContext = nint.Zero;
        }

        if (_eglSurface != Egl.EGL_NO_SURFACE && _angle is not null)
        {
            _ = Egl.DestroySurface(_angle.Display, _eglSurface);
            _eglSurface = Egl.EGL_NO_SURFACE;
        }

        _angle?.Dispose();
        _renderTexture?.Dispose();
        _swapChain?.Dispose();
        _deviceContext?.Dispose();
        _device?.Dispose();

        _frameAvailable.Dispose();
        _cancellation.Dispose();
    }
}

/// <summary>A NUL-terminated ASCII string in native memory, for render params.</summary>
internal readonly unsafe struct Utf8RenderString : IDisposable
{
    private readonly nint _pointer;

    internal Utf8RenderString(string value)
    {
        _pointer = Marshal.StringToHGlobalAnsi(value);
    }

    internal nint Pointer => _pointer;

    public void Dispose()
    {
        if (_pointer != nint.Zero)
        {
            Marshal.FreeHGlobal(_pointer);
        }
    }
}

/// <summary>Render-API entry points, declared where the render layer uses them.</summary>
internal static unsafe class MpvRenderNative
{
    private const string LibraryName = "mpv";

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_create")]
    internal static extern int ContextCreate(out nint context, nint handle, MpvRenderParam* parameters);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_render")]
    internal static extern int ContextRender(nint context, MpvRenderParam* parameters);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_update")]
    internal static extern ulong ContextUpdate(nint context);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_set_update_callback")]
    internal static extern void ContextSetUpdateCallback(
        nint context,
        delegate* unmanaged[Cdecl]<nint, void> callback,
        nint callbackContext);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_report_swap")]
    internal static extern void ContextReportSwap(nint context);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_free")]
    internal static extern void ContextFree(nint context);
}

/// <summary>Constants shared with the engine's own interop.</summary>
internal static class MpvNativeConstants
{
    /// <summary>Bit set by <c>mpv_render_context_update</c> when a frame is ready.</summary>
    internal const ulong UpdateFrameFlag = 1UL;
}
