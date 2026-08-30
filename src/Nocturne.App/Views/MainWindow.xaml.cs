using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Nocturne.App.Services;
using Nocturne.App.ViewModels;
using Nocturne.Core.Media;
using Nocturne.Core.Playback;
using Nocturne.Core.Text;
using Nocturne.Engine.Client;
using Nocturne.Engine.Interop;
using Nocturne.Engine.Thumbnails;
using Nocturne.Render.Pipeline;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT;
using WinRT.Interop;

namespace Nocturne.App.Views;

/// <summary>The player window.</summary>
/// <remarks>
/// Owns the engine and the renderer, wires the swap chain into the
/// <c>SwapChainPanel</c>, and translates input into view-model calls. It holds
/// no playback state of its own.
/// </remarks>
public sealed partial class MainWindow : Window, IDisposable
{
    private PlayerEngine? _engine;
    private VideoRenderer? _renderer;
    private bool _isFullScreen;
    private OverlappedPresenterState? _stateBeforeFullScreen;
    private bool _renderPipelineFailed;

    /// <summary>A launch file waiting for the render pipeline to exist.</summary>
    private string? _deferredLaunchPath;

    private ThumbnailSource? _thumbnails;
    private WriteableBitmap? _previewBitmap;
    private string? _previewPath;
    private bool _isPointerOnSeekBar;

    /// <summary>Creates the window and starts the engine.</summary>
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        Title = "Nocturne";

        ApplyInitialSize();

        ApplyTitleBarColors();

        _engine = new PlayerEngine(EngineOptions.Default);

        // libmpv's own account of what it is doing. When the picture is missing
        // but the sound is not, this is where the reason appears — the video
        // output and the hardware decoder both report here.
        _engine.LogMessage += (_, message) =>
            DiagnosticLog.Current.Write($"mpv/{message.Level}", $"{message.Prefix}: {message.Text}");

        // What was actually decoded, recorded when the video output is configured
        // for it. Two files in the same container behaving differently is a
        // colour question far more often than a codec one, and this is the line
        // that answers it — but only from here. Hooked to FileLoaded, which is
        // the obvious place, it ran before the video output existed and every
        // field came back as a question mark.
        // The preview decoder is per file, and the path is only known once one
        // is loaded. Raised on the engine's thread, so it hops before touching
        // anything the window owns.
        _engine.FileLoaded += (_, _) =>
        {
            string? path = _engine?.Snapshot.Source;
            DispatcherQueue.TryEnqueue(() => StartThumbnails(path));
        };

        _engine.VideoConfigured += (_, _) =>
        {
            try
            {
                DiagnosticLog.Current.Write("video", _engine.DescribeVideo());
            }
#pragma warning disable CA1031 // A log line must never take the engine's thread down.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                DiagnosticLog.Current.WriteException("video", ex);
            }
        };

        DiagnosticLog.Current.Write(
            "engine",
            $"libmpv client API {MpvRuntime.ApiVersion.Major}.{MpvRuntime.ApiVersion.Minor}");

        ViewModel = new PlayerViewModel(_engine, DispatcherQueue);

        VideoPanel.CompositionScaleChanged += OnVideoPanelScaleChanged;

        AttachSliderGestures();
        AttachAccelerators();
        Closed += OnClosed;
    }

    /// <summary>
    /// Attaches the pointer handlers the sliders would otherwise swallow.
    /// </summary>
    /// <remarks>
    /// <c>Slider</c> has a class handler that marks <c>PointerPressed</c> and
    /// <c>PointerReleased</c> as handled so it can capture the pointer for a
    /// drag. A handler attached in XAML never runs for an already-handled
    /// event, so the scrub suppression looked wired up and silently did nothing.
    /// <c>AddHandler</c> with <c>handledEventsToo</c> is the only way to see them.
    /// </remarks>
    private void AttachSliderGestures()
    {
        SeekBar.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSeekBarPointerPressed), handledEventsToo: true);
        SeekBar.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnSeekBarPointerReleased), handledEventsToo: true);

        // Capture loss commits, it does not cancel. A Slider gives up pointer
        // capture as part of an ordinary click, so this fires on every seek —
        // treating it as an abandoned gesture put the pre-seek position back and
        // the thumb sprang backwards under the pointer each time. The view model
        // ignores whichever of the two arrives second.
        SeekBar.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnSeekBarPointerReleased), handledEventsToo: true);

        // A genuine cancellation — a touch the system took away — is the one
        // case where the gesture really should be abandoned without seeking.
        SeekBar.AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnSeekBarPointerCancelled), handledEventsToo: true);

        SeekBar.KeyDown += OnSeekBarKeyDown;

        // Hover and drag both drive the preview, and a Slider marks pointer
        // events handled, so these need the same handledEventsToo treatment as
        // the scrub handlers above.
        SeekBar.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnSeekBarPointerMoved), handledEventsToo: true);
        SeekBar.AddHandler(UIElement.PointerEnteredEvent,
            new PointerEventHandler(OnSeekBarPointerEntered), handledEventsToo: true);
        SeekBar.AddHandler(UIElement.PointerExitedEvent,
            new PointerEventHandler(OnSeekBarPointerExited), handledEventsToo: true);

        VolumeSlider.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnVolumePointerPressed), handledEventsToo: true);
        VolumeSlider.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnVolumePointerReleased), handledEventsToo: true);
        VolumeSlider.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnVolumePointerReleased), handledEventsToo: true);
    }

    /// <summary>
    /// Registers shortcuts as accelerators rather than as key-down handlers.
    /// </summary>
    /// <remarks>
    /// <c>KeyDown</c> only fires when something inside the tree holds focus. At
    /// startup the transport bar is collapsed and nothing focusable is left, so
    /// every shortcut would be dead until the user clicked something — and once
    /// they had clicked a transport button, Space would re-press that button
    /// instead of toggling playback. Accelerators fire regardless of focus.
    /// </remarks>
    private void AttachAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                action();
                args.Handled = true;
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }

        Add(VirtualKey.Space, VirtualKeyModifiers.None, () => ViewModel.TogglePlayPause());
        Add(VirtualKey.Left, VirtualKeyModifiers.None, () => ViewModel.SeekBy(-SeekMath.DefaultStep));
        Add(VirtualKey.Right, VirtualKeyModifiers.None, () => ViewModel.SeekBy(SeekMath.DefaultStep));
        Add(VirtualKey.Left, VirtualKeyModifiers.Shift, () => ViewModel.SeekBy(-SeekMath.FineStep));
        Add(VirtualKey.Right, VirtualKeyModifiers.Shift, () => ViewModel.SeekBy(SeekMath.FineStep));
        Add(VirtualKey.F11, VirtualKeyModifiers.None, ToggleFullScreen);
        Add(VirtualKey.Escape, VirtualKeyModifiers.None, () =>
        {
            if (_isFullScreen)
            {
                ToggleFullScreen();
            }
        });
        Add(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = OpenFileAsync());
    }

    /// <summary>State shown by the window.</summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>Opens the file the app was launched with.</summary>
    public void OpenOnLaunch(string path)
    {
        // Held back until the render pipeline exists, and this is the whole of a
        // real bug rather than caution.
        //
        // A file association opens a file the instant the window is created,
        // while the pipeline is still waiting for the panel's first layout pass
        // to learn its size. libmpv reaches video-output initialisation, finds
        // no render context, and does not wait or retry — it logs "No render
        // context set", deselects the video track outright and plays the file as
        // audio. The window then shows a swap chain nothing has ever drawn into,
        // which is undefined and comes out white.
        //
        // That is why the same file played correctly when opened from inside the
        // app and blank when double-clicked in Explorer: it was never about the
        // file.
        if (_renderer is null && !_renderPipelineFailed)
        {
            DiagnosticLog.Current.Write(
                "nocturne", $"deferring until the pipeline is ready: {path}");
            _deferredLaunchPath = path;
            return;
        }

        ViewModel.Open(path);
    }

    /// <summary>
    /// Opens the file the app was launched with, once video can accept it.
    /// </summary>
    /// <remarks>
    /// Runs whether the pipeline was built or failed. A pipeline that could not
    /// be built is a reason to play the file without video, not a reason to
    /// leave the app sitting on an empty window holding a path it never opened.
    /// </remarks>
    private void OpenDeferredLaunchFile()
    {
        if (_deferredLaunchPath is not { } path)
        {
            return;
        }

        _deferredLaunchPath = null;
        DiagnosticLog.Current.Write("nocturne", $"opening deferred file: {path}");
        ViewModel.Open(path);
    }

    /// <summary>
    /// Gives the window a real starting size.
    /// </summary>
    /// <remarks>
    /// Without this the window sizes itself to its content, and the content is a
    /// grid whose only non-collapsed child may be a small message. The first
    /// build to reach a real machine opened as a 610x155 sliver holding nothing
    /// but an error card — the star-sized rows had nothing to stretch against,
    /// so they measured to the card.
    /// <para>
    /// Set through <c>AppWindow</c> rather than through XAML because
    /// <c>Window</c> in WinUI has no Width or Height property to set.
    /// </para>
    /// </remarks>
    private void ApplyInitialSize()
    {
        // Physical pixels. A 16:9 window on a 100% display; on a 150% display
        // Windows scales it, which is the behaviour a user expects from a
        // remembered size they have not yet chosen.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 760));
    }

    /// <summary>
    /// Paints the system caption buttons to match the window.
    /// </summary>
    /// <remarks>
    /// With <c>ExtendsContentIntoTitleBar</c> the caption buttons keep their own
    /// background, which shows as a light rectangle in the corner of an
    /// otherwise black window.
    /// </remarks>
    private void ApplyTitleBarColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            // Older Windows 10 builds reject these setters outright, and this
            // runs in the constructor — a throw here means the window never
            // appears at all.
            return;
        }

        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
    }

    /// <summary>
    /// Builds the render pipeline once the panel has a real size.
    /// </summary>
    /// <remarks>
    /// Deferred to the first size-changed rather than done in the constructor:
    /// a <c>SwapChainPanel</c> reports zero dimensions until it has been laid
    /// out, and a swap chain cannot be created at zero by zero.
    /// </remarks>
    /// <summary>
    /// Rebuilds the surface when the display scale changes without the layout.
    /// </summary>
    /// <remarks>
    /// Dragging the window from a 100% display to a 150% one usually leaves the
    /// panel the same size in DIPs, so <c>SizeChanged</c> never fires — while
    /// the number of physical pixels behind it has grown by half. Without this
    /// the swap chain keeps the old pixel size and the video is upscaled by the
    /// compositor: exactly the softness the CompositionScale factor below exists
    /// to avoid, arrived at by a different route.
    /// </remarks>
    private void OnVideoPanelScaleChanged(SwapChainPanel sender, object args) =>
        ResizeSurfaceToPanel(VideoPanel.ActualWidth, VideoPanel.ActualHeight);

    private void OnVideoPanelSizeChanged(object sender, SizeChangedEventArgs e) =>
        ResizeSurfaceToPanel(e.NewSize.Width, e.NewSize.Height);

    private void ResizeSurfaceToPanel(double widthInDips, double heightInDips)
    {
        if (_engine is null || _renderPipelineFailed)
        {
            return;
        }

        // Physical pixels, not DIPs. CompositionScale carries the display scale
        // and any transform on the panel; using DIPs renders the video at 67% of
        // the surface on a 150% display and then upscales it back.
        int width = (int)Math.Round(widthInDips * VideoPanel.CompositionScaleX);
        int height = (int)Math.Round(heightInDips * VideoPanel.CompositionScaleY);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_renderer is null)
        {
            TryCreateRenderer(width, height);
            return;
        }

        _renderer.Resize(width, height);
    }

    private void TryCreateRenderer(int width, int height)
    {
        try
        {
            TryCreateRendererCore(width, height);
        }
        finally
        {
            // Every exit, including the early one and the failure one: the file
            // this app was launched with has been waiting on this decision.
            OpenDeferredLaunchFile();
        }
    }

    private void TryCreateRendererCore(int width, int height)
    {
        // A previous run went into the native pipeline and never came out. Going
        // back in would repeat it, and an app that force-closes on launch cannot
        // even be used to play sound or to read the log that explains why.
        if (RenderGuard.PreviousAttemptFailed)
        {
            _renderPipelineFailed = true;
            DiagnosticLog.Current.Write(
                "render",
                "skipped: the previous run crashed while building the pipeline");

            ViewModel.ReportRenderFailure(
                "Video is off for this run because the last attempt to start it crashed. " +
                "Audio still works. Delete " +
                (RenderGuard.MarkerPath ?? "the marker file in the Nocturne data folder") +
                " to try again.",
                DiagnosticLog.Current.Path);
            return;
        }

        try
        {
            DiagnosticLog.Current.Write("render", $"building pipeline at {width}x{height}");

            // Bracketing starts here, not around the managed call: what is being
            // guarded against is the process dying inside Direct3D, ANGLE or
            // libmpv, where no catch block below will ever run.
            RenderGuard.BeginAttempt();

            _renderer = VideoRenderer.Create(
                _engine!.NativeHandle, width, height,
                step => DiagnosticLog.Current.Write("render", step));

            // Hand the swap chain to the panel. This is the join between the
            // Direct3D pipeline and the XAML tree, and it is what allows the
            // transport bar to compose over the video with real transparency.
            ISwapChainPanelNative native = VideoPanel.As<ISwapChainPanelNative>();
            Marshal.ThrowExceptionForHR(native.SetSwapChain(_renderer.SwapChain.NativePointer));

            _renderer.RenderFailed += OnRenderFailed;

            // The attempt is not over yet. Create returns while the render
            // thread is still starting, and the heaviest native work happens
            // after that — so clearing the marker here would call the attempt a
            // success before the part most likely to fault had run, and a crash
            // on the first frame would repeat on every launch with the guard
            // none the wiser. It is cleared when a frame actually reaches the
            // screen, and on a clean shutdown for the case where the app is
            // opened and closed without ever playing anything.
            _renderer.FirstFramePresented += (_, _) =>
            {
                DiagnosticLog.Current.Write("render", "first frame presented");
                RenderGuard.EndAttempt();
            };
        }
#pragma warning disable CA1031 // A missing GPU path must degrade, not crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _renderPipelineFailed = true;
            _renderer?.Dispose();
            _renderer = null;

            DiagnosticLog.Current.WriteException("render", ex);
            ViewModel.ReportRenderFailure(ex.Message, DiagnosticLog.Current.Path);

            // An exception means the process survived, which is the whole
            // question the marker answers. A managed failure needs no guard.
            RenderGuard.EndAttempt();
        }
    }

    private void OnRenderFailed(object? sender, Exception error)
    {
        DiagnosticLog.Current.WriteException("render", error);
        DispatcherQueue.TryEnqueue(() =>
            ViewModel.ReportRenderFailure(error.Message, DiagnosticLog.Current.Path));
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => ViewModel.TogglePlayPause();

    private void OnNextClick(object sender, RoutedEventArgs e) => ViewModel.Next();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => ViewModel.Previous();

    private void OnMuteClick(object sender, RoutedEventArgs e) => ViewModel.ToggleMute();

    private void OnSubtitlesClick(object sender, RoutedEventArgs e)
    {
        // Track selection lands in Milestone 2; see PLAN.md.
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    /// <summary>
    /// Double-clicking the picture enters and leaves full screen.
    /// </summary>
    /// <remarks>
    /// Every video player does this, so people arrive already expecting it and
    /// try it before looking for a button. Marked handled so the gesture does
    /// not also reach the window, where a double-click means maximize.
    /// </remarks>
    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ToggleFullScreen();
        e.Handled = true;
    }

    private void OnSeekBarPointerPressed(object sender, PointerRoutedEventArgs e) => ViewModel.BeginScrub();

    private void OnSeekBarPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.EndScrub(SeekBar.Value);

        if (!_isPointerOnSeekBar)
        {
            HidePreview();
        }
    }

    private void OnSeekBarPointerCancelled(object sender, PointerRoutedEventArgs e) =>
        ViewModel.CancelScrub();

    // ── Scrub preview ────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a preview decoder for the file that just loaded.
    /// </summary>
    /// <remarks>
    /// Keyed on the path so that a repeat of the same file — a replay, or the
    /// playlist looping back — does not tear down a working decoder and build an
    /// identical one.
    /// </remarks>
    private void StartThumbnails(string? path)
    {
        if (string.Equals(path, _previewPath, StringComparison.Ordinal))
        {
            return;
        }

        StopThumbnails();
        _previewPath = path;

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var source = new ThumbnailSource(path);
            source.FrameReady += OnThumbnailReady;
            source.Failed += OnThumbnailFailed;

            (int width, int height) = source.Size;
            _previewBitmap = new WriteableBitmap(width, height);
            PreviewImage.Source = _previewBitmap;

            _thumbnails = source;
        }
#pragma warning disable CA1031 // A preview that cannot start must not affect playback.
        catch (Exception error)
#pragma warning restore CA1031
        {
            DiagnosticLog.Current.WriteException("preview", error);
        }
    }

    private void StopThumbnails()
    {
        if (_thumbnails is null)
        {
            return;
        }

        _thumbnails.FrameReady -= OnThumbnailReady;
        _thumbnails.Failed -= OnThumbnailFailed;
        _thumbnails.Dispose();
        _thumbnails = null;

        HidePreview();
    }

    private void OnThumbnailFailed(object? sender, Exception error) =>
        DiagnosticLog.Current.WriteException("preview", error);

    /// <summary>Copies a decoded frame into the on-screen bitmap.</summary>
    /// <remarks>
    /// Raised on the decoder's worker thread, so everything here happens after a
    /// hop. The frame is dropped rather than queued if the pointer has since
    /// left the bar: it is a picture of somewhere nobody is looking any more.
    /// </remarks>
    private void OnThumbnailReady(object? sender, ThumbnailFrame frame)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_previewBitmap is null || PreviewCard.Visibility != Visibility.Visible)
            {
                return;
            }

            try
            {
                using Stream pixels = _previewBitmap.PixelBuffer.AsStream();
                pixels.Write(frame.Pixels, 0, frame.Pixels.Length);
                _previewBitmap.Invalidate();
            }
#pragma warning disable CA1031 // Same again: a preview is never worth a crash.
            catch (Exception error)
#pragma warning restore CA1031
            {
                DiagnosticLog.Current.WriteException("preview", error);
            }
        });
    }

    private void OnSeekBarPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOnSeekBar = true;
        UpdatePreview(e);
    }

    private void OnSeekBarPointerMoved(object sender, PointerRoutedEventArgs e) => UpdatePreview(e);

    private void OnSeekBarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOnSeekBar = false;

        // A drag that has left the bar vertically is still a drag, and the
        // preview is the only thing showing where it will land.
        if (!ViewModel.IsScrubbing)
        {
            HidePreview();
        }
    }

    /// <summary>
    /// Moves the preview card under the pointer and asks for the frame there.
    /// </summary>
    /// <remarks>
    /// The timecode is written from the pointer, not from the frame that comes
    /// back. They disagree for as long as a decode takes, and that is the right
    /// way round: the label answers "where am I about to seek", which is known
    /// immediately, while the picture answers "what is there", which is not.
    /// </remarks>
    private void UpdatePreview(PointerRoutedEventArgs e)
    {
        TimeSpan duration = ViewModel.Duration;
        if (_thumbnails is null || duration <= TimeSpan.Zero || SeekBar.ActualWidth <= 0)
        {
            HidePreview();
            return;
        }

        double x = e.GetCurrentPoint(SeekBar).Position.X;
        double fraction = Math.Clamp(x / SeekBar.ActualWidth, 0.0, 1.0);
        TimeSpan position = SeekMath.ClampToRange(duration * fraction, duration);

        PreviewTimecode.Text = Timecode.Format(position);
        PreviewCard.Visibility = Visibility.Visible;

        PlacePreview(x);
        _thumbnails.Request(position);
    }

    /// <summary>Centres the card on the pointer, without letting it leave the window.</summary>
    private void PlacePreview(double pointerX)
    {
        Point origin = SeekBar
            .TransformToVisual(Stage)
            .TransformPoint(new Point(pointerX, 0));

        // ActualWidth is zero until the card has been measured once, which is
        // exactly the first time it is shown. The padded image width is the
        // right answer for that one frame.
        double width = PreviewCard.ActualWidth > 0 ? PreviewCard.ActualWidth : 200;

        const double Edge = 8;
        double maximum = Math.Max(Edge, Stage.ActualWidth - width - Edge);

        PreviewOffset.X = Math.Clamp(origin.X - (width / 2), Edge, maximum);
    }

    private void HidePreview()
    {
        _isPointerOnSeekBar = false;
        PreviewCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Seeks with the arrow keys without letting the slider move itself.
    /// </summary>
    /// <remarks>
    /// The commit deliberately does not go through <c>ValueChanged</c>.
    /// <c>RangeBase.ValueChanged</c> fires for programmatic writes as well as
    /// for gestures and carries nothing that separates them, so committing from
    /// there turns every position update the engine publishes into a fresh
    /// seek — a feedback loop that makes playback unwatchable the moment the
    /// user touches the bar.
    /// </remarks>
    private void OnSeekBarKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool fine = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        TimeSpan step = fine ? SeekMath.FineStep : SeekMath.DefaultStep;

        switch (e.Key)
        {
            case VirtualKey.Left:
                ViewModel.SeekBy(-step);
                break;
            case VirtualKey.Right:
                ViewModel.SeekBy(step);
                break;
            case VirtualKey.Home:
                ViewModel.SeekTo(TimeSpan.Zero);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnVolumePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.BeginVolumeAdjust();

        // The click itself is a change, and it has already happened: the
        // Slider's own class handler moves the thumb to the pointer and raises
        // ValueChanged before this instance handler runs, so that first
        // ValueChanged arrives while the gesture flag is still false and is
        // discarded. Committing here is what makes a single click on the track
        // set the volume, instead of moving the thumb and then springing back.
        ViewModel.SetVolume(VolumeSlider.Value);
    }

    private void OnVolumePointerReleased(object sender, PointerRoutedEventArgs e) =>
        ViewModel.EndVolumeAdjust();

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Only a gesture commits. Outside one, this event is the binding writing
        // the engine's own value back in, and forwarding that would fight the
        // slider every time libmpv echoes a value.
        if (ViewModel.IsAdjustingVolume)
        {
            ViewModel.SetVolume(e.NewValue);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        // Take the deferral before the first await: without it the drag source
        // is told the drop finished and may release the data while it is still
        // being read.
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            IReadOnlyList<Windows.Storage.IStorageItem> items = await e.DataView.GetStorageItemsAsync();

            // Indexed directly rather than through FirstOrDefault: this is an
            // IReadOnlyList, so the LINQ path allocates an enumerator to reach
            // an element that is one indexer away (CA1826).
            if (items.Count > 0 && items[0] is Windows.Storage.StorageFile file)
            {
                ViewModel.Open(file.Path);
            }
        }
#pragma warning disable CA1031 // An async void handler must not let anything escape.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // A virtual drag source — a file inside a zip, a mail attachment, an
            // app that has since closed — throws here. This is async void, so an
            // escaping exception reaches the unhandled handler and kills the
            // process over a bad drop.
            ViewModel.ReportTransientError($"Could not read the dropped item: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;

        // The title bar row has a fixed height and stays in the layout when the
        // presenter changes, so without this a full-screen film keeps a 40px
        // black band with a file name in it across the top.
        TitleBarGrid.Visibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;

        if (_isFullScreen)
        {
            // Remembered because SetPresenter(Overlapped) returns a *default*
            // overlapped presenter, not the one that was in use. A maximized
            // window that goes full screen and comes back would otherwise come
            // back restored, having quietly lost the state the user chose.
            _stateBeforeFullScreen = (AppWindow.Presenter as OverlappedPresenter)?.State;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            return;
        }

        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

        if (_stateBeforeFullScreen is { } state
            && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            switch (state)
            {
                case OverlappedPresenterState.Maximized:
                    presenter.Maximize();
                    break;
                case OverlappedPresenterState.Minimized:
                    presenter.Minimize();
                    break;
                default:
                    break;
            }
        }

        _stateBeforeFullScreen = null;
    }

    /// <summary>Shows the file picker and opens what the user chose.</summary>
    /// <remarks>
    /// The app is unpackaged, so the picker has no window to parent itself to
    /// and throws unless it is handed the window handle explicitly.
    /// </remarks>
    private async Task OpenFileAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            // FileTypeFilter must not be empty or PickSingleFileAsync throws.
            foreach (string extension in MediaFormats.VideoExtensions.Concat(MediaFormats.AudioExtensions))
            {
                picker.FileTypeFilter.Add(extension);
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                ViewModel.Open(file.Path);
            }
        }
#pragma warning disable CA1031 // The picker is an OS surface; it must not crash the app.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ViewModel.ReportTransientError($"Could not open the file picker: {ex.Message}");
        }
    }

    private void OnClosed(object sender, WindowEventArgs args) => Dispose();

    /// <summary>
    /// Releases the engine and the render pipeline.
    /// </summary>
    /// <remarks>
    /// A <c>Window</c> is not normally disposable, but this one owns two native
    /// resources whose lifetime has to end with the window. Closing is the only
    /// caller; <see cref="Dispose"/> exists so that ownership is visible in the
    /// type rather than implied by an event handler.
    /// </remarks>
    public void Dispose()
    {
        // Reaching a clean shutdown is itself proof the render path did not take
        // the process down, and it covers the session where the app was opened
        // and closed without a frame ever being drawn.
        RenderGuard.EndAttempt();

        // Order matters: the renderer holds a libmpv render context that must be
        // freed before the handle it was created from.
        bool renderStateLeaked = false;
        if (_renderer is not null)
        {
            // Unsubscribed after disposal, not before. Disposal is exactly when
            // the renderer reports a render thread that would not stop, and
            // unsubscribing first threw that one report away — the failure most
            // worth knowing about was the only one guaranteed to go unheard.
            _renderer.Dispose();
            renderStateLeaked = _renderer.LeakedNativeState;
            _renderer.RenderFailed -= OnRenderFailed;
            _renderer = null;
        }

        StopThumbnails();
        ViewModel.Dispose();

        if (renderStateLeaked)
        {
            // The render context could not be freed, so the handle it belongs to
            // must not be destroyed: render.h requires the context to go first,
            // and mpv_terminate_destroy with a live context asserts or hangs.
            // Leaving both alive leaks them until the process exits, which is
            // the next thing that happens.
            DiagnosticLog.Current.Write(
                "render",
                "render thread would not stop; leaving the libmpv handle alive rather than " +
                "destroying it under a live render context");

            _engine = null;
            return;
        }

        _engine?.Dispose();
        _engine = null;
    }
}

/// <summary>
/// Attaches a Direct3D swap chain to a <c>SwapChainPanel</c>.
/// </summary>
/// <remarks>
/// Declared by hand because the Windows App SDK does not project it. The GUID is
/// from <c>windows.ui.xaml.media.dxinterop.h</c>.
/// </remarks>
[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    /// <summary>Binds a swap chain to the panel, or clears it when null.</summary>
    [PreserveSig]
    int SetSwapChain(nint swapChain);
}
