using System.Globalization;
using Nocturne.Engine.Client;
using Nocturne.Engine.Interop;

namespace Nocturne.Engine.Thumbnails;

/// <summary>
/// Decodes preview frames for a file, without disturbing playback.
/// </summary>
/// <remarks>
/// <para>
/// A second libmpv instance, running the software renderer. It is a second
/// instance because the first one is playing: seeking the playing instance to
/// fetch a preview is the thing this feature must never do, and no amount of
/// care afterwards would put back the position it moved.
/// </para>
/// <para>
/// It is the <em>software</em> renderer for the same reason. The GPU path is
/// one device, one swap chain and one render context, and threading a second
/// consumer through it to produce 256-pixel-wide images would put the part of
/// this project that took longest to make work at risk for the part that
/// matters least. Decoding a keyframe to a small CPU buffer costs a few
/// milliseconds and shares nothing.
/// </para>
/// <para>
/// Requests are coalesced rather than queued. A drag along the seek bar
/// produces pointer events far faster than frames can be decoded, and every one
/// of them supersedes the last — a queue would spend its time rendering
/// positions the pointer left long ago.
/// </para>
/// </remarks>
public sealed unsafe class ThumbnailSource : IDisposable
{
    /// <summary>Rows are this many bytes; a multiple of 64, as render.h asks.</summary>
    private readonly int _stride;

    private readonly int _width;
    private readonly int _height;
    private readonly string _path;

    private readonly AutoResetEvent _requested = new(initialState: false);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _worker;
    private readonly object _gate = new();

    private MpvClient? _client;
    private nint _context;
    private nint _buffer;

    private TimeSpan? _wanted;
    private bool _disposed;

    /// <summary>
    /// Starts a preview decoder for one file.
    /// </summary>
    /// <param name="path">The file to preview. The same one that is playing.</param>
    /// <param name="width">Preview width in pixels. Rounded up to a multiple of 16.</param>
    /// <param name="height">Preview height in pixels.</param>
    public ThumbnailSource(string path, int width = 256, int height = 144)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 16);

        _path = path;

        // Rounded so the stride lands on a multiple of 64 without padding the
        // rows, which keeps the delivered array tightly packed and lets the
        // consumer copy it in one go.
        _width = (width + 15) / 16 * 16;
        _height = height;
        _stride = _width * 4;

        _worker = new Thread(Run)
        {
            Name = "Nocturne thumbnails",
            IsBackground = true,

            // Below normal on purpose. This is a convenience, and it must lose
            // to playback and to the UI whenever the machine is busy.
            Priority = ThreadPriority.BelowNormal,
        };

        _worker.Start();
    }

    /// <summary>Raised when a preview frame is ready. Raised on a worker thread.</summary>
    public event EventHandler<ThumbnailFrame>? FrameReady;

    /// <summary>Raised once if the preview decoder could not be started.</summary>
    /// <remarks>
    /// Previews are optional. A failure here must leave the player working and
    /// say why in the log, not surface as an error the user has to dismiss.
    /// </remarks>
    public event EventHandler<Exception>? Failed;

    /// <summary>The preview size, after rounding.</summary>
    public (int Width, int Height) Size => (_width, _height);

    /// <summary>
    /// Asks for a preview at a position, replacing any request not yet started.
    /// </summary>
    public void Request(TimeSpan position)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _wanted = position;
        }

        try
        {
            _requested.Set();
        }
        catch (ObjectDisposedException)
        {
            // Disposal raced this call. A missed preview is not worth a throw on
            // the caller's thread, which is the UI thread.
        }
    }

    private void Run()
    {
        try
        {
            Start();
        }
#pragma warning disable CA1031 // Previews are optional; a failure must not escape this thread.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Failed?.Invoke(this, error);
            return;
        }

        try
        {
            Loop();
        }
#pragma warning disable CA1031 // Same again.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Failed?.Invoke(this, error);
        }
        finally
        {
            Teardown();
        }
    }

    private void Start()
    {
        _client = new MpvClient(BuildOptions());

        _buffer = (nint)(void*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(
            (nuint)(_stride * _height), alignment: 64);

        CreateRenderContext();

        // The render context first, the file second, and never the other way
        // round. libmpv initialises video output when a file loads: with no
        // render context set at that moment it does not wait or retry, it logs
        // "No render context set", deselects the video track and plays on
        // without it. That is a bug this project has already shipped once, on
        // the playback path, where it presented as a blank white picture.
        _client.Command("loadfile", _path, "replace");
    }

    private void CreateRenderContext()
    {
        using Utf8Buffer apiType = Utf8.Allocate(MpvSoftwareRenderNative.ApiTypeSoftware);

        MpvRenderParam* parameters = stackalloc MpvRenderParam[2];
        parameters[0] = new MpvRenderParam
        {
            Type = MpvRenderParamType.ApiType,
            Data = (nint)apiType.Pointer,
        };
        parameters[1] = new MpvRenderParam
        {
            Type = MpvRenderParamType.Invalid,
            Data = nint.Zero,
        };

        int result = MpvSoftwareRenderNative.ContextCreate(out nint context, _client!.Handle, parameters);
        if (result < 0)
        {
            throw new MpvException(
                (MpvError)result,
                "create a software render context for previews");
        }

        _context = context;
    }

    private void Loop()
    {
        CancellationToken token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            // A timeout as well as a signal: the signal can be consumed by a
            // request that arrives while a render is already running, and
            // without the timeout that request would wait for the next one.
            _ = _requested.WaitOne(TimeSpan.FromMilliseconds(200));

            if (token.IsCancellationRequested)
            {
                return;
            }

            TimeSpan? target;
            lock (_gate)
            {
                target = _wanted;
                _wanted = null;
            }

            if (target is { } position)
            {
                Capture(position, token);
            }
        }
    }

    private void Capture(TimeSpan position, CancellationToken token)
    {
        // Keyframe seeks, not exact ones. An exact seek decodes every frame from
        // the preceding keyframe to the target, which for a long GOP is tens of
        // frames for a picture that is about to be replaced by the next pointer
        // move. Landing on the nearest keyframe is what every player's scrub
        // preview does, and it is why they feel instant.
        _client!.Command(
            "seek",
            position.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
            "absolute+keyframes");

        if (!WaitForFrame(token))
        {
            return;
        }

        int[] size = [_width, _height];
        nuint stride = (nuint)_stride;

        using Utf8Buffer format = Utf8.Allocate("bgr0");

        fixed (int* sizePointer = size)
        {
            MpvRenderParam* parameters = stackalloc MpvRenderParam[5];
            parameters[0] = new MpvRenderParam
            {
                Type = MpvRenderParamType.SoftwareSize,
                Data = (nint)sizePointer,
            };
            parameters[1] = new MpvRenderParam
            {
                Type = MpvRenderParamType.SoftwareFormat,
                Data = (nint)format.Pointer,
            };
            parameters[2] = new MpvRenderParam
            {
                Type = MpvRenderParamType.SoftwareStride,
                Data = (nint)(&stride),
            };
            parameters[3] = new MpvRenderParam
            {
                Type = MpvRenderParamType.SoftwarePointer,
                Data = _buffer,
            };
            parameters[4] = new MpvRenderParam
            {
                Type = MpvRenderParamType.Invalid,
                Data = nint.Zero,
            };

            if (MpvSoftwareRenderNative.ContextRender(_context, parameters) < 0)
            {
                return;
            }
        }

        // Copied out rather than handed over. The buffer is native, reused on
        // every capture, and the consumer is on another thread that will still
        // be reading it when the next drag position arrives.
        var pixels = new byte[_stride * _height];
        System.Runtime.InteropServices.Marshal.Copy(_buffer, pixels, 0, pixels.Length);

        // The fourth byte of bgr0 is padding, and libmpv leaves it at zero. Every
        // surface that will display this reads that byte as alpha, so left alone
        // the picture is perfectly decoded and completely invisible. Filled here,
        // on this thread, rather than on the one drawing the frame.
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
        }

        FrameReady?.Invoke(this, new ThumbnailFrame(position, _width, _height, pixels));
    }

    /// <summary>
    /// Waits for libmpv to have a frame ready, or gives up.
    /// </summary>
    /// <remarks>
    /// Polled rather than driven by the update callback. The callback exists to
    /// wake a render loop running at display rate; here there is one frame
    /// wanted at a time and a hard deadline, and polling every two milliseconds
    /// for at most a fifth of a second is both simpler and bounded — a file the
    /// decoder cannot seek in must not wedge this thread.
    /// </remarks>
    private bool WaitForFrame(CancellationToken token)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            if ((MpvSoftwareRenderNative.ContextUpdate(_context) & 1UL) != 0)
            {
                return true;
            }

            Thread.Sleep(2);
        }

        return false;
    }

    private static Dictionary<string, string> BuildOptions() => new(StringComparer.Ordinal)
    {
        // Init-only. The render API, so frames come here instead of to a window.
        ["vo"] = "libmpv",

        // Nothing but pictures. Audio would fight the playing instance for the
        // output device, and subtitles are not wanted on a thumbnail.
        ["audio"] = "no",
        ["sid"] = "no",

        // Software decoding on purpose, even though the machine has a decoder.
        // A second hardware decode session competes with playback for a fixed
        // number of hardware surfaces, and the frames wanted here are small
        // enough that the CPU is not the constraint.
        ["hwdec"] = "no",

        // Decode at the lowest quality that still identifies a scene: the loop
        // filter is a large share of the cost and invisible at this size.
        ["vd-lavc-skiploopfilter"] = "all",
        ["vd-lavc-fast"] = "yes",
        ["vd-lavc-threads"] = "1",

        // No scaling work beyond what the target size needs. The quality
        // settings the player uses for playback would be spent on an image the
        // size of a postage stamp.
        ["scale"] = "bilinear",
        ["cscale"] = "bilinear",
        ["dscale"] = "bilinear",
        ["dither"] = "no",
        ["correct-downscaling"] = "no",
        ["sigmoid-upscaling"] = "no",
        ["deband"] = "no",

        // Paused from the start and kept open at the end: this instance never
        // plays anything, it only ever sits on a frame someone asked for.
        ["pause"] = "yes",
        ["keep-open"] = "yes",

        ["osd-level"] = "0",
        ["osc"] = "no",
        ["input-default-bindings"] = "no",
        ["input-vo-keyboard"] = "no",
        ["config"] = "no",
        ["ytdl"] = "no",
        ["terminal"] = "no",
        ["load-scripts"] = "no",
    };

    private void Teardown()
    {
        if (_context != nint.Zero)
        {
            // Before the handle, as render.h requires.
            MpvSoftwareRenderNative.ContextFree(_context);
            _context = nint.Zero;
        }

        _client?.Dispose();
        _client = null;

        if (_buffer != nint.Zero)
        {
            System.Runtime.InteropServices.NativeMemory.AlignedFree((void*)_buffer);
            _buffer = nint.Zero;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();

        try
        {
            _requested.Set();
        }
        catch (ObjectDisposedException)
        {
        }

        // The worker owns every native handle and frees them on its way out, so
        // this waits rather than freeing anything itself. The bound is here for
        // the same reason it is on the playback renderer: a wedged decoder must
        // not stop the window from closing.
        _ = _worker.Join(TimeSpan.FromSeconds(2));

        _requested.Dispose();
        _cancellation.Dispose();
    }
}
