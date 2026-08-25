using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nocturne.App.ViewModels;
using Nocturne.Engine.Client;
using Nocturne.Render.Pipeline;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinRT;

namespace Nocturne.App.Views;

/// <summary>The player window.</summary>
/// <remarks>
/// Owns the engine and the renderer, wires the swap chain into the
/// <c>SwapChainPanel</c>, and translates input into view-model calls. It holds
/// no playback state of its own.
/// </remarks>
public sealed partial class MainWindow : Window
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

        ApplyTitleBarColors();

        _engine = new PlayerEngine(EngineOptions.Default);
        ViewModel = new PlayerViewModel(_engine, DispatcherQueue);

        RootGrid.KeyDown += OnKeyDown;
        Closed += OnClosed;
    }

    /// <summary>State shown by the window.</summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>Opens the file the app was launched with.</summary>
    public void OpenOnLaunch(string path) => ViewModel.Open(path);

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
            _renderer = VideoRenderer.Create(_engine!.NativeHandle, width, height);

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

            ViewModel.ErrorMessage = ex.Message;
            ViewModel.ErrorVisibility = Visibility.Visible;
            ViewModel.EmptyStateVisibility = Visibility.Collapsed;
        }
    }

    private void OnRenderFailed(object? sender, Exception error) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.ErrorMessage = error.Message;
            ViewModel.ErrorVisibility = Visibility.Visible;
        });

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

    private void OnSeekBarValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Keyboard and accessibility changes arrive without a pointer gesture, so
        // they commit immediately rather than waiting for a release that will
        // never come.
        if (!SeekBar.FocusState.Equals(FocusState.Unfocused))
        {
            ViewModel.EndScrub(e.NewValue);
        }
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        ViewModel.SetVolume(e.NewValue);

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
            if (items.FirstOrDefault() is Windows.Storage.StorageFile file)
            {
                ViewModel.Open(file.Path);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space:
                ViewModel.TogglePlayPause();
                e.Handled = true;
                break;

            case VirtualKey.Left:
                ViewModel.SeekBy(-Nocturne.Core.Playback.SeekMath.DefaultStep);
                e.Handled = true;
                break;

            case VirtualKey.Right:
                ViewModel.SeekBy(Nocturne.Core.Playback.SeekMath.DefaultStep);
                e.Handled = true;
                break;

            case VirtualKey.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;

            case VirtualKey.Escape when _isFullScreen:
                ToggleFullScreen();
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        AppWindow.SetPresenter(_isFullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
    }

    private void OnClosed(object sender, WindowEventArgs args)
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
