using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nocturne.Engine.Interop;
using Nocturne.Render.Interop;
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

    /// <summary>Borrowed from <see cref="_angle"/>, which owns and disposes it.</summary>
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
    private bool _hasPresented;
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
    /// Whether disposal had to abandon native state instead of freeing it.
    /// </summary>
    /// <remarks>
    /// True only when the render thread would not stop. The caller must then
    /// <em>not</em> destroy the libmpv handle: <c>render.h</c> requires the
    /// render context to be freed first, and this object has just declined to
    /// free it because a live thread is still using it. Destroying the handle
    /// anyway trades a leak that lasts until the process exits — which is
    /// moments away — for an assertion or a hang inside libmpv on the way out.
    /// </remarks>
    public bool LeakedNativeState { get; private set; }

    /// <summary>Raised once, after the first frame reaches the screen.</summary>
    /// <remarks>
    /// Raised on the render thread. Building the pipeline and drawing through it
    /// are different things: construction returns while the render thread is
    /// still starting, and the heaviest native work — libmpv compiling its
    /// shaders, the driver's first real draw — happens afterwards. Anything that
    /// wants to know the pipeline actually works has to wait for this rather
    /// than for the constructor.
    /// </remarks>
    public event EventHandler? FirstFramePresented;

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
    /// <param name="trace">
    /// Receives one line per initialization stage. The pipeline has six stages
    /// that can each fail for unrelated reasons, and on a machine the author
    /// cannot reach, knowing which one stopped is most of the diagnosis.
    /// </param>
    /// <exception cref="RenderInitializationException">Any stage failed.</exception>
    public static VideoRenderer Create(nint mpvHandle, int width, int height, Action<string>? trace = null)
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

        // SetDllImportResolver is scoped to one assembly. MpvRenderNative lives
        // here, not in Nocturne.Engine, so without this registration every
        // P/Invoke below probes for a bare "mpv.dll" — a file that exists in no
        // libmpv distribution — and throws on the first render call.
        MpvRuntime.RegisterInteropAssembly(typeof(VideoRenderer).Assembly);

        var renderer = new VideoRenderer(key);
        try
        {
            renderer.Initialize(mpvHandle, Math.Max(1, width), Math.Max(1, height), trace);
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
        // A layout pass can reach a renderer that has already been disposed —
        // this is a public method, and _frameAvailable.Set() on a disposed event
        // throws on the caller's thread, which here is the UI thread.
        if (_disposed || width <= 0 || height <= 0)
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

    private void Initialize(nint mpvHandle, int width, int height, Action<string>? trace)
    {
        void Step(string message) => trace?.Invoke(message);

        _pendingWidth = width;
        _pendingHeight = height;
        _surfaceWidth = width;
        _surfaceHeight = height;

        Step($"surface {width}x{height}");

        // Before anything is built: say what the native runtime is, and load it
        // one library at a time with a line on each side. When the process dies
        // inside a native entry point there is no exception and no stack — the
        // last line written is the entire diagnosis, so it has to be specific.
        NativePreflight.Run(Step);

        // ANGLE first, and the Direct3D device comes out of it. Which of the two
        // creates the device is decided by what the ANGLE build supports, so the
        // decision cannot be made here; see AngleContext.
        _angle = AngleContext.Create(trace);
        _device = _angle.Device;
        _deviceContext = _device.ImmediateContext;

        CreateSwapChain(width, height);
        Step("composition swap chain created");

        CreateRenderSurface(width, height);
        Step("render texture wrapped as an EGL pbuffer");

        // The context must be current on the thread that creates the mpv render
        // context, and on every thread that renders. The render thread makes it
        // current again as its first act.
        _angle.MakeCurrent(_eglSurface);
        Step("EGL context made current");

        CreateMpvRenderContext(mpvHandle);
        Step("mpv render context created");

        _angle.ClearCurrent();
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
        (_renderTexture, _eglSurface) = BuildRenderSurface(width, height);
        _surfaceWidth = width;
        _surfaceHeight = height;
    }

    /// <summary>
    /// Builds a render texture and the EGL surface that wraps it, touching no
    /// state of its own.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CreateRenderSurface"/> so a resize can build the
    /// replacement before letting go of what it already has. Tearing the old
    /// surface down first means a failure halfway through leaves the renderer
    /// with neither — and the render thread then dies holding EGL_NO_SURFACE,
    /// which turns disposal into a call to mpv_render_context_free with no
    /// current context.
    /// </remarks>
    private (ID3D11Texture2D Texture, nint Surface) BuildRenderSurface(int width, int height)
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

        ID3D11Texture2D texture = _device.CreateTexture2D(description);

        // No EGL_WIDTH/EGL_HEIGHT here. With buftype EGL_D3D_TEXTURE_ANGLE the
        // size comes from the texture itself, and some ANGLE builds reject those
        // two attributes with EGL_BAD_ATTRIBUTE. They belong to
        // EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE, where the size cannot be
        // queried from the handle.
        int* surfaceAttributes = stackalloc int[]
        {
            Egl.EGL_TEXTURE_FORMAT, Egl.EGL_TEXTURE_RGBA,
            Egl.EGL_TEXTURE_TARGET, Egl.EGL_TEXTURE_2D,
            Egl.EGL_NONE,
        };

        nint surface = Egl.CreatePbufferFromClientBuffer(
            _angle.Display,
            Egl.EGL_D3D_TEXTURE_ANGLE,
            texture.NativePointer,
            _angle.Config,
            surfaceAttributes);

        if (surface == Egl.EGL_NO_SURFACE)
        {
            texture.Dispose();
            throw new RenderInitializationException(
                "eglCreatePbufferFromClientBuffer refused the render texture " +
                $"({description.Format}, {width}x{height}): {Egl.DescribeLastError()}. " +
                "Either this ANGLE build lacks EGL_ANGLE_d3d_texture_client_buffer, " +
                "or the texture format does not match the chosen EGL config.");
        }

        return (texture, surface);
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

        // Zero, and the reasoning that says otherwise is a trap worth stating.
        //
        // GL's framebuffer origin is bottom-left and Direct3D's is top-left, so
        // the obvious conclusion is that something must flip. Something does —
        // ANGLE. The surface here is a pbuffer wrapping a D3D11 texture, and
        // ANGLE's D3D backend already inverts the viewport when it translates
        // GL into Direct3D, so the texture comes out in Direct3D's orientation
        // with no help. Asking libmpv to flip as well applies the correction
        // twice, which is exactly once too many: the first build that ever drew
        // a frame drew it upside down.
        int flipY = 0;

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
        //
        // The result is checked because this is where a lost device shows up.
        // After a TDR, Present returns DXGI_ERROR_DEVICE_REMOVED for the rest of
        // the process's life; discarding that means the loop keeps calling
        // ContextReportSwap below, feeding libmpv a steady rhythm of swaps that
        // never reached a screen. The picture freezes, the sound plays on, and
        // nothing anywhere says why.
        _swapChain.Present(1, PresentFlags.None).CheckError();

        // libmpv derives its frame scheduling from these reports. Skipping them
        // is what turns 23.976 fps content into visible judder.
        MpvRenderNative.ContextReportSwap(_renderContext);

        if (!_hasPresented)
        {
            _hasPresented = true;
            FirstFramePresented?.Invoke(this, EventArgs.Empty);
        }
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

        // Build the replacement before giving up what is already working, so a
        // failure anywhere below leaves a renderer that still has a valid
        // surface rather than one holding EGL_NO_SURFACE. The render texture is
        // not a back buffer, so holding it does not block ResizeBuffers.
        (ID3D11Texture2D texture, nint surface) = BuildRenderSurface(width, height);

        try
        {
            _angle.ClearCurrent();

            // Checked, not discarded. Vortice returns a Result here rather than
            // throwing, and a device removed by a driver reset or a TDR fails
            // exactly here. Carrying on would leave the swap chain at the old
            // size and the render texture at the new one, and CopyResource
            // between two different sizes is undefined — in practice the runtime
            // drops it and the picture freezes with nothing logged anywhere.
            _swapChain.ResizeBuffers(
                BufferCount,
                (uint)width,
                (uint)height,
                Format.B8G8R8A8_UNorm,
                SwapChainFlags.None).CheckError();
        }
        catch
        {
            _ = Egl.DestroySurface(_angle.Display, surface);
            texture.Dispose();

            // Put the context back on the surface still in use, so the thread
            // that unwinds through here leaves the renderer as it found it.
            if (_eglSurface != Egl.EGL_NO_SURFACE)
            {
                _angle.MakeCurrent(_eglSurface);
            }

            throw;
        }

        if (_eglSurface != Egl.EGL_NO_SURFACE)
        {
            _ = Egl.DestroySurface(_angle.Display, _eglSurface);
        }

        _renderTexture?.Dispose();

        _renderTexture = texture;
        _eglSurface = surface;
        _surfaceWidth = width;
        _surfaceHeight = height;

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

        try
        {
            renderer?._frameAvailable.Set();
        }
        catch (ObjectDisposedException)
        {
            // Disposal can complete between the lookup above and this line. An
            // exception escaping an [UnmanagedCallersOnly] method fails the
            // whole process fast, so a missed wake-up on a renderer that is
            // going away is swallowed on purpose.
        }
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

        bool threadStopped = !_renderThread.IsAlive || _renderThread.Join(TimeSpan.FromSeconds(2));
        if (!threadStopped)
        {
            // The render thread is wedged — stuck in Present behind a hung
            // driver, or mid-frame on something very large. Everything below
            // frees handles that thread is still using, so it is skipped: this
            // leaks a device and a render context for the remaining life of the
            // process, which is strictly better than a use-after-free inside the
            // GPU driver with no usable stack. The comment used to claim this
            // and the code did the opposite.
            //
            // Recorded rather than merely reported, because it changes what the
            // caller is allowed to do next: the libmpv handle must now outlive
            // this object too.
            LeakedNativeState = true;

            RenderFailed?.Invoke(
                this,
                new TimeoutException(
                    "The video render thread did not stop within two seconds; " +
                    "GPU resources were deliberately leaked rather than freed underneath it."));
            return;
        }

        if (_renderContext != nint.Zero)
        {
            // render.h requires the GL context to be current for
            // mpv_render_context_free — it destroys the shaders, FBOs and
            // texture cache it created. The render thread cleared the context on
            // its way out, so it has to be made current again here, on the
            // thread that is about to call free.
            bool contextCurrent = false;
            try
            {
                _angle.MakeCurrent(_eglSurface);
                contextCurrent = true;
            }
#pragma warning disable CA1031 // Teardown must continue whatever happens here.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                RenderFailed?.Invoke(this, ex);
            }

            MpvRenderNative.ContextFree(_renderContext);
            _renderContext = nint.Zero;

            if (contextCurrent)
            {
                _angle.ClearCurrent();
            }
        }

        if (_eglSurface != Egl.EGL_NO_SURFACE && _angle is not null)
        {
            _ = Egl.DestroySurface(_angle.Display, _eglSurface);
            _eglSurface = Egl.EGL_NO_SURFACE;
        }

        // The ANGLE context owns the Direct3D device, so it goes last: everything
        // above holds Direct3D objects that must be released while the device
        // they came from is still alive. _device itself is not disposed here —
        // it is borrowed, and AngleContext.Dispose releases it.
        _renderTexture?.Dispose();
        _swapChain?.Dispose();
        _deviceContext?.Dispose();
        _angle?.Dispose();

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
