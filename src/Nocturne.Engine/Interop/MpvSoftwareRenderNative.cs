using System.Runtime.InteropServices;

namespace Nocturne.Engine.Interop;

/// <summary>
/// The render-API entry points needed by the software renderer.
/// </summary>
/// <remarks>
/// Declared here rather than shared with the OpenGL ones in
/// <c>Nocturne.Render</c> because <c>SetDllImportResolver</c> is scoped to a
/// single assembly, and because the software path is the reason
/// <c>Nocturne.Engine</c> can produce pictures at all without Windows: it needs
/// no GPU, no device, and no window, so it stays on the platform-neutral side of
/// the project.
/// </remarks>
internal static unsafe class MpvSoftwareRenderNative
{
    private const string LibraryName = "mpv";

    /// <summary>API type string selecting the software renderer.</summary>
    internal const string ApiTypeSoftware = "sw";

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_create")]
    internal static extern int ContextCreate(out nint context, nint handle, MpvRenderParam* parameters);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_render")]
    internal static extern int ContextRender(nint context, MpvRenderParam* parameters);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_update")]
    internal static extern ulong ContextUpdate(nint context);

    [DllImport(LibraryName, EntryPoint = "mpv_render_context_free")]
    internal static extern void ContextFree(nint context);
}
