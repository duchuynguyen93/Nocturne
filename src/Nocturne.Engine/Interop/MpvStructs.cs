using System.Runtime.InteropServices;

namespace Nocturne.Engine.Interop;

/// <summary>
/// Header of every event libmpv returns from <c>mpv_wait_event</c>.
/// </summary>
/// <remarks>
/// Mirrors <c>mpv_event</c>. The two <see cref="int"/> fields sit adjacent in C
/// and the 64-bit <see cref="ReplyUserData"/> follows on an 8-byte boundary, so
/// the natural sequential layout matches without explicit offsets. The pointer
/// returned by <c>mpv_wait_event</c> stays owned by libmpv and is only valid
/// until the next call on the same handle, which is why the pump copies out of
/// it immediately rather than storing the pointer.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserData;
    public nint Data;
}

/// <summary>
/// Payload of <see cref="MpvEventId.PropertyChange"/> and property replies.
/// </summary>
/// <remarks>
/// Mirrors <c>mpv_event_property</c>. <see cref="Data"/> points at a value of
/// the shape named by <see cref="Format"/>: a <c>double</c> for
/// <see cref="MpvFormat.Double"/>, an <c>int</c> for
/// <see cref="MpvFormat.Flag"/>, and a <c>char**</c> — a pointer to a pointer —
/// for <see cref="MpvFormat.String"/>. Reading a string therefore needs two
/// dereferences, and getting that wrong reads a pointer as text.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public nint Name;
    public MpvFormat Format;
    public nint Data;
}

/// <summary>Payload of <see cref="MpvEventId.EndFile"/>.</summary>
/// <remarks>Mirrors <c>mpv_event_end_file</c>.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public int PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

/// <summary>Payload of <see cref="MpvEventId.LogMessage"/>.</summary>
/// <remarks>Mirrors <c>mpv_event_log_message</c>.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public nint Prefix;
    public nint Level;
    public nint Text;
    public int LogLevel;
}

/// <summary>One entry in a render parameter array.</summary>
/// <remarks>Mirrors <c>mpv_render_param</c>.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct MpvRenderParam
{
    /// <summary>Which slot this entry fills.</summary>
    public MpvRenderParamType Type;

    /// <summary>Pointer to the value, whose shape depends on <see cref="Type"/>.</summary>
    public nint Data;
}

/// <summary>
/// Initialization parameters for the OpenGL render backend.
/// </summary>
/// <remarks>
/// Mirrors <c>mpv_opengl_init_params</c>. <see cref="GetProcAddress"/> must stay
/// reachable from managed code for the whole life of the render context —
/// libmpv calls it again whenever it compiles a shader, not only during setup.
/// Letting the delegate be collected produces a crash inside the GPU driver that
/// carries no hint of its managed cause.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct MpvOpenGlInitParams
{
    /// <summary>Resolves a GL entry point by name.</summary>
    public nint GetProcAddress;

    /// <summary>Opaque context handed back to <see cref="GetProcAddress"/>.</summary>
    public nint GetProcAddressContext;
}

/// <summary>
/// Describes the framebuffer libmpv should draw the next frame into.
/// </summary>
/// <remarks>Mirrors <c>mpv_opengl_fbo</c>.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct MpvOpenGlFbo
{
    /// <summary>GL framebuffer name; 0 means the default framebuffer.</summary>
    public int Fbo;

    /// <summary>Target width in pixels.</summary>
    public int Width;

    /// <summary>Target height in pixels.</summary>
    public int Height;

    /// <summary>
    /// GL internal format of the target, or 0 to let libmpv assume 8-bit RGBA.
    /// </summary>
    /// <remarks>
    /// This must be set for a 10-bit target. Leaving it at 0 makes libmpv dither
    /// for 8 bits, which throws away the extra precision an HDR pipeline exists
    /// to carry and reintroduces the banding it was meant to remove.
    /// </remarks>
    public int InternalFormat;
}
