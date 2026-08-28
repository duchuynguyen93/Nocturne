using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace Nocturne.Render.Pipeline;

/// <summary>
/// Reports on the native runtime before the pipeline tries to use it.
/// </summary>
/// <remarks>
/// <para>
/// Every stage of the frame path crosses into a native library, and a fault in
/// one of those is not an exception — it is an <c>abort()</c> or an access
/// violation that ends the process outright. The only evidence left behind is
/// whatever reached the log before it happened, so the log has to be fine
/// grained enough that the last line names the thing that died.
/// </para>
/// <para>
/// This class exists because "it stopped somewhere between two log lines" cost
/// several rounds of guessing, each one paid for by someone installing a build
/// on a machine nobody here can reach. Every step below is logged on both sides:
/// a missing "…ok" line is as informative as the line itself.
/// </para>
/// <para>
/// Nothing here is required for playback. It loads libraries the pipeline is
/// about to load anyway and reads descriptions that have no effect on anything.
/// </para>
/// </remarks>
internal static class NativePreflight
{
    /// <summary>
    /// The native libraries the pipeline depends on, in load order.
    /// </summary>
    /// <remarks>
    /// The mingw runtime is listed even though nothing here calls into it: it is
    /// what <c>libEGL</c> imports, and a mismatched copy found on <c>PATH</c>
    /// rather than beside the executable is exactly the failure this is meant to
    /// make visible.
    /// </remarks>
    private static readonly string[] Libraries =
    [
        "libgcc_s_seh-1.dll",
        "libwinpthread-1.dll",
        "libstdc++-6.dll",
        "zlib1.dll",
        "libGLESv2.dll",
        "libEGL.dll",
        "libmpv-2.dll",
    ];

    private const uint LoadWithAlteredSearchPath = 0x00000008;

    /// <summary>
    /// Loads a library and reports why, in Windows' own terms, if it will not.
    /// </summary>
    /// <remarks>
    /// <see cref="NativeLibrary.TryLoad(string, out nint)"/> answers only yes or
    /// no, and "no" covers a missing file, a wrong architecture and an
    /// unsatisfied dependency — which need completely different fixes. This
    /// route keeps the Win32 code, and <c>126 ERROR_MOD_NOT_FOUND</c> on a file
    /// that demonstrably exists says "something it imports is missing" in one
    /// number.
    /// </remarks>
    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "LoadLibraryExW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern nint LoadLibraryEx(string fileName, nint reserved, uint flags);

    /// <summary>
    /// Logs what is present, then loads each library one at a time.
    /// </summary>
    /// <param name="step">Receives one line per observation.</param>
    internal static void Run(Action<string> step)
    {
        ArgumentNullException.ThrowIfNull(step);

        DescribeAdapters(step);

        string directory = AppContext.BaseDirectory;
        step($"native runtime directory: {directory}");

        foreach (string name in Libraries)
        {
            LoadOne(directory, name, step);
        }

        step("preflight complete");
    }

    /// <summary>
    /// Loads one library by full path and says so before and after.
    /// </summary>
    /// <remarks>
    /// By full path deliberately. A bare name lets the loader search
    /// <c>PATH</c>, and a same-named library from another toolchain is the
    /// documented cause of the <c>ERROR_BAD_EXE_FORMAT</c> this project has
    /// already hit once. If the file beside the executable is not the one that
    /// loads, that is the bug, and logging the resolved path is how it is seen.
    /// </remarks>
    private static void LoadOne(string directory, string name, Action<string> step)
    {
        string path = Path.Combine(directory, name);

        if (!File.Exists(path))
        {
            step($"{name}: MISSING from the application directory");
            return;
        }

        var file = new FileInfo(path);
        string version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "no version resource";
        step($"{name}: {file.Length} bytes, {version}");

        // The line that matters is the one after this. If the log ends here, the
        // process died inside this library's entry point — which is not a failure
        // the loader reports, and not something any catch block sees.
        step($"{name}: loading");

        try
        {
            // LOAD_WITH_ALTERED_SEARCH_PATH so the library's own directory is
            // searched for its dependencies. That is what makes the copies
            // shipped beside the executable win over any same-named library
            // elsewhere on PATH.
            nint handle = LoadLibraryEx(path, nint.Zero, LoadWithAlteredSearchPath);
            if (handle != nint.Zero)
            {
                step($"{name}: loaded at 0x{handle.ToString("X", CultureInfo.InvariantCulture)}");

                // Not freed. The pipeline is about to use these, and unloading
                // only to reload them changes what is being tested.
                return;
            }

            int error = Marshal.GetLastWin32Error();
            string reason = error switch
            {
                126 => "ERROR_MOD_NOT_FOUND — the file is there, but something it imports is not",
                193 => "ERROR_BAD_EXE_FORMAT — wrong architecture, or a 32-bit copy won the search",
                _ => new System.ComponentModel.Win32Exception(error).Message,
            };

            step($"{name}: FAILED TO LOAD, Win32 {error}: {reason}");
        }
#pragma warning disable CA1031 // Preflight reports; it never decides.
        catch (Exception error)
#pragma warning restore CA1031
        {
            step($"{name}: threw {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Names the graphics adapters, without creating a Direct3D device.
    /// </summary>
    /// <remarks>
    /// Enumerating through DXGI is far cheaper and far safer than
    /// <c>D3D11CreateDevice</c>, and it is the only way to learn which GPU and
    /// which driver a report came from. When device creation is itself the thing
    /// that dies, this is the last line before it.
    /// </remarks>
    private static void DescribeAdapters(Action<string> step)
    {
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint index = 0; ; index++)
            {
                if (factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Failure || adapter is null)
                {
                    break;
                }

                using (adapter)
                {
                    AdapterDescription1 description = adapter.Description1;
                    step(
                        $"adapter {index}: {description.Description}, " +
                        $"vendor 0x{description.VendorId:X4} device 0x{description.DeviceId:X4}, " +
                        $"{description.DedicatedVideoMemory / (1024 * 1024)} MB dedicated, " +
                        $"flags {description.Flags}");
                }
            }
        }
#pragma warning disable CA1031 // Same again: this is a report, not a decision.
        catch (Exception error)
#pragma warning restore CA1031
        {
            step($"adapter enumeration failed: {error.GetType().Name}: {error.Message}");
        }
    }
}
