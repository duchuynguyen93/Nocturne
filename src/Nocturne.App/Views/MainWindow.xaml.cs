using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nocturne.App.Services;
using Nocturne.App.ViewModels;
using Nocturne.Core.Media;
using Nocturne.Core.Playback;
using Nocturne.Engine.Client;
using Nocturne.Render.Pipeline;
using Windows.ApplicationModel.DataTransfer;
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
    private bool _renderPipelineFailed;

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

        DiagnosticLog.Current.Write(
            "engine",
            $"libmpv client API {PlayerEngine.ApiVersion.Major}.{PlayerEngine.ApiVersion.Minor}");

        ViewModel = new PlayerViewModel(_engine, DispatcherQueue);

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

        // A drag interrupted by an alt-tab or a cancelled touch never sends a
        // release. Without these the suppression flag latches on and the seek
        // bar stops following playback for the rest of the session.
        SeekBar.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnSeekBarPointerCancelled), handledEventsToo: true);
        SeekBar.AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnSeekBarPointerCancelled), handledEventsToo: true);

        SeekBar.KeyDown += OnSeekBarKeyDown;

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
    public void OpenOnLaunch(string path) => ViewModel.Open(path);

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
    private void OnVideoPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_engine is null || _renderPipelineFailed)
        {
            return;
        }

        // Physical pixels, not DIPs. CompositionScale carries the display scale
        // and any transform on the panel; using DIPs renders the video at 67% of
        // the surface on a 150% display and then upscales it back.
        int width = (int)Math.Round(e.NewSize.Width * VideoPanel.CompositionScaleX);
        int height = (int)Math.Round(e.NewSize.Height * VideoPanel.CompositionScaleY);

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
            DiagnosticLog.Current.Write("render", $"building pipeline at {width}x{height}");
            _renderer = VideoRenderer.Create(
                _engine!.NativeHandle, width, height,
                step => DiagnosticLog.Current.Write("render", step));

            // Hand the swap chain to the panel. This is the join between the
            // Direct3D pipeline and the XAML tree, and it is what allows the
            // transport bar to compose over the video with real transparency.
            ISwapChainPanelNative native = VideoPanel.As<ISwapChainPanelNative>();
            Marshal.ThrowExceptionForHR(native.SetSwapChain(_renderer.SwapChain.NativePointer));

            _renderer.RenderFailed += OnRenderFailed;
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

    private void OnSeekBarPointerPressed(object sender, PointerRoutedEventArgs e) => ViewModel.BeginScrub();

    private void OnSeekBarPointerReleased(object sender, PointerRoutedEventArgs e) =>
        ViewModel.EndScrub(SeekBar.Value);

    private void OnSeekBarPointerCancelled(object sender, PointerRoutedEventArgs e) =>
        ViewModel.CancelScrub();

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

    private void OnVolumePointerPressed(object sender, PointerRoutedEventArgs e) =>
        ViewModel.BeginVolumeAdjust();

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
            ViewModel.ErrorMessage = $"Could not read the dropped item: {ex.Message}";
            ViewModel.ErrorVisibility = Visibility.Visible;
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

        AppWindow.SetPresenter(_isFullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
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
            ViewModel.ErrorMessage = $"Could not open the file picker: {ex.Message}";
            ViewModel.ErrorVisibility = Visibility.Visible;
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
        // Order matters: the renderer holds a libmpv render context that must be
        // freed before the handle it was created from.
        if (_renderer is not null)
        {
            _renderer.RenderFailed -= OnRenderFailed;
            _renderer.Dispose();
            _renderer = null;
        }

        ViewModel.Dispose();
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
