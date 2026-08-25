namespace Nocturne.Engine.Client;

/// <summary>
/// The libmpv option set the player initializes with.
/// </summary>
/// <remarks>
/// <para>
/// This class is the picture-quality specification, expressed as the options
/// that produce it. Nothing else in the app affects how a frame looks. It is
/// written out longhand, with a reason per line, because every one of these is a
/// decision someone will otherwise silently undo while chasing an unrelated bug.
/// </para>
/// <para>
/// Options marked "init-only" below are read once by <c>mpv_initialize</c> and
/// ignored afterwards. Setting them as properties at runtime returns success and
/// does nothing, which is the single most common way to lose hardware decoding
/// without noticing.
/// </para>
/// </remarks>
public sealed record EngineOptions
{
    /// <summary>The defaults the app ships with.</summary>
    public static readonly EngineOptions Default = new();

    /// <summary>
    /// Whether to keep the core alive at end of file instead of unloading.
    /// </summary>
    /// <remarks>
    /// The app owns the playlist, not libmpv. With this off, libmpv tears down
    /// the file at EOF and the last frame vanishes before the app has decided
    /// what to do next.
    /// </remarks>
    public bool KeepOpen { get; init; } = true;

    /// <summary>Hardware decoder selection. Init-only.</summary>
    /// <remarks>
    /// <c>d3d11va</c> keeps decoded surfaces on the GPU for the whole path.
    /// The <c>-copy</c> variants read every frame back to system memory, which
    /// shows up as steady CPU use and a warm laptop during 4K playback. Falling
    /// back to <c>auto-safe</c> is handled by libmpv itself when the GPU cannot
    /// decode a given codec.
    /// </remarks>
    public string HardwareDecoding { get; init; } = "d3d11va,auto-safe";

    /// <summary>Luma upscaler. </summary>
    /// <remarks>
    /// <c>ewa_lanczossharp</c> is the quality reference for upscaling; it is
    /// what separates a 1080p file on a 4K panel from looking soft. It costs
    /// noticeably more GPU time than <c>bilinear</c>, which is the whole point.
    /// </remarks>
    public string Scale { get; init; } = "ewa_lanczossharp";

    /// <summary>Chroma upscaler.</summary>
    public string ChromaScale { get; init; } = "ewa_lanczossharp";

    /// <summary>Downscaler, used when the window is smaller than the video.</summary>
    /// <remarks>
    /// A sharp kernel that is right for upscaling causes ringing on downscale,
    /// so this deliberately differs from <see cref="Scale"/>.
    /// </remarks>
    public string DownScale { get; init; } = "mitchell";

    /// <summary>Whether to remove banding introduced by 8-bit source encoding.</summary>
    /// <remarks>
    /// Costs a little GPU time and fixes the visible stepping in dark gradients
    /// that is the most common complaint about otherwise good encodes.
    /// </remarks>
    public bool Deband { get; init; } = true;

    /// <summary>
    /// Whether to resample playback timing to the display's actual refresh rate.
    /// </summary>
    /// <remarks>
    /// This is the option that removes judder from 23.976 fps content on a 60 Hz
    /// panel. It requires accurate presentation feedback, which is why the render
    /// layer must call <c>mpv_render_context_report_swap</c> after every present.
    /// </remarks>
    public bool DisplayResample { get; init; } = true;

    /// <summary>Whether to interpolate frames when the ratio is not integral.</summary>
    public bool Interpolation { get; init; } = true;

    /// <summary>
    /// Whether to tell the display driver about the source's colour volume.
    /// </summary>
    /// <remarks>
    /// Required for HDR passthrough: without it the swap chain is told nothing
    /// about the content and Windows tone-maps HDR into SDR before it reaches
    /// the panel, which is exactly the loss the pipeline exists to avoid.
    /// </remarks>
    public bool TargetColorspaceHint { get; init; } = true;

    /// <summary>Whether to take exclusive control of the audio device.</summary>
    /// <remarks>
    /// Exclusive mode bypasses the Windows mixer and is required for bitstream
    /// passthrough of DTS-HD and TrueHD. It also means no other application can
    /// make a sound while the player holds the device, so it is off by default
    /// and offered as a setting rather than assumed.
    /// </remarks>
    public bool ExclusiveAudio { get; init; }

    /// <summary>Minimum libmpv log level forwarded to the app's log.</summary>
    public string LogLevel { get; init; } = "warn";

    /// <summary>
    /// Renders the options as the string pairs <c>mpv_set_option_string</c> takes.
    /// </summary>
    /// <remarks>
    /// <c>vo=libmpv</c> is not negotiable: it is what puts libmpv into render-API
    /// mode so the app owns presentation. Any other value makes libmpv create its
    /// own window and the composition layer has nothing to compose.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ToMpvOptions()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Init-only. Puts libmpv in render-API mode; the app presents.
            ["vo"] = "libmpv",

            // Init-only. gpu-next is the libplacebo renderer: better tone
            // mapping, dithering, and scaling than the legacy gpu path.
            ["gpu-api"] = "opengl",
            ["vd-lavc-dr"] = "yes",
            ["hwdec"] = HardwareDecoding,

            ["keep-open"] = KeepOpen ? "yes" : "no",
            ["keep-open-pause"] = "yes",

            ["scale"] = Scale,
            ["cscale"] = ChromaScale,
            ["dscale"] = DownScale,

            // Downscaling in linear light with the correct transfer function.
            // Skipping these makes bright edges bloom when the window is small.
            ["correct-downscaling"] = "yes",
            ["linear-downscaling"] = "yes",
            ["sigmoid-upscaling"] = "yes",

            ["deband"] = Deband ? "yes" : "no",
            ["dither-depth"] = "auto",

            ["video-sync"] = DisplayResample ? "display-resample" : "audio",
            ["interpolation"] = Interpolation ? "yes" : "no",
            ["tscale"] = "oversample",

            ["target-colorspace-hint"] = TargetColorspaceHint ? "yes" : "no",

            // Subtitles: prefer the embedded ASS styling the author chose over
            // the player's own, and load sidecars from the file's own directory.
            ["sub-auto"] = "fuzzy",
            ["sub-ass-override"] = "no",
            ["blend-subtitles"] = "yes",

            // The app draws its own transport bar. libmpv's OSD would render a
            // second, unstyled one on top of it.
            ["osd-level"] = "0",
            ["osc"] = "no",
            ["input-default-bindings"] = "no",
            ["input-vo-keyboard"] = "no",

            // The app owns config; reading the user's global mpv.conf would make
            // playback depend on state the app cannot see or support.
            ["config"] = "no",
            ["ytdl"] = "no",

            // Terminal output has nowhere to go in a windowed app and costs a
            // formatting pass per line.
            ["terminal"] = "no",
        };

        if (ExclusiveAudio)
        {
            options["audio-exclusive"] = "yes";
        }

        return options;
    }
}
