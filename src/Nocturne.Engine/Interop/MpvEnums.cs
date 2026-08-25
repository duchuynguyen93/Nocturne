namespace Nocturne.Engine.Interop;

/// <summary>Value shapes libmpv can read and write for a property.</summary>
/// <remarks>Mirrors <c>mpv_format</c> in <c>client.h</c>.</remarks>
public enum MpvFormat
{
    /// <summary>No value.</summary>
    None = 0,

    /// <summary>UTF-8 string.</summary>
    String = 1,

    /// <summary>String already formatted for on-screen display.</summary>
    OsdString = 2,

    /// <summary>Boolean, marshalled as a 32-bit int.</summary>
    Flag = 3,

    /// <summary>64-bit signed integer.</summary>
    Int64 = 4,

    /// <summary>Double-precision float.</summary>
    Double = 5,

    /// <summary>Dynamically typed node, used for track lists and similar.</summary>
    Node = 6,
}

/// <summary>Events libmpv delivers through <c>mpv_wait_event</c>.</summary>
/// <remarks>
/// Mirrors <c>mpv_event_id</c>. The deprecated ids between 9 and 15 are omitted
/// deliberately; libmpv no longer raises them and naming them would suggest the
/// app handles cases that cannot occur.
/// </remarks>
public enum MpvEventId
{
    /// <summary>No event; returned when a wait times out.</summary>
    None = 0,

    /// <summary>The core is shutting down and the handle must be released.</summary>
    Shutdown = 1,

    /// <summary>A log line, delivered only after <c>mpv_request_log_messages</c>.</summary>
    LogMessage = 2,

    /// <summary>Reply to an asynchronous property read.</summary>
    GetPropertyReply = 3,

    /// <summary>Reply to an asynchronous property write.</summary>
    SetPropertyReply = 4,

    /// <summary>Reply to an asynchronous command.</summary>
    CommandReply = 5,

    /// <summary>The core began opening a playlist entry.</summary>
    StartFile = 6,

    /// <summary>A playlist entry stopped, successfully or not.</summary>
    EndFile = 7,

    /// <summary>The file is open and its tracks and duration are known.</summary>
    FileLoaded = 8,

    /// <summary>A message sent by a script or by <c>client-message</c>.</summary>
    ClientMessage = 16,

    /// <summary>Video output parameters changed.</summary>
    VideoReconfig = 17,

    /// <summary>Audio output parameters changed.</summary>
    AudioReconfig = 18,

    /// <summary>A seek started.</summary>
    Seek = 20,

    /// <summary>Playback resumed after a seek or an initial load.</summary>
    PlaybackRestart = 21,

    /// <summary>An observed property changed.</summary>
    PropertyChange = 22,

    /// <summary>The event queue overflowed and events were dropped.</summary>
    QueueOverflow = 24,

    /// <summary>A registered hook is waiting to be continued.</summary>
    Hook = 25,
}

/// <summary>Why a playlist entry stopped.</summary>
/// <remarks>Mirrors <c>mpv_end_file_reason</c>.</remarks>
public enum MpvEndFileReason
{
    /// <summary>Played to the end.</summary>
    Eof = 0,

    /// <summary>Stopped by a command.</summary>
    Stop = 2,

    /// <summary>The player is quitting.</summary>
    Quit = 3,

    /// <summary>Stopped because of an error; the event carries the code.</summary>
    Error = 4,

    /// <summary>Superseded by a redirect, such as a playlist expanding.</summary>
    Redirect = 5,
}

/// <summary>Parameter slots accepted by the render API.</summary>
/// <remarks>Mirrors <c>mpv_render_param_type</c> in <c>render.h</c>.</remarks>
public enum MpvRenderParamType
{
    /// <summary>Terminator for a parameter array.</summary>
    Invalid = 0,

    /// <summary>Selects the rendering backend, as a UTF-8 string.</summary>
    ApiType = 1,

    /// <summary>Backing store for <c>mpv_opengl_init_params</c>.</summary>
    OpenGlInitParams = 2,

    /// <summary>Backing store for <c>mpv_opengl_fbo</c>.</summary>
    OpenGlFbo = 3,

    /// <summary>Whether the target surface has its origin at the bottom.</summary>
    FlipY = 4,

    /// <summary>Enables render-ahead and frame timing control.</summary>
    AdvancedControl = 10,

    /// <summary>Requests timing information about the next frame.</summary>
    NextFrameInfo = 11,

    /// <summary>Blocks until the frame's target presentation time.</summary>
    BlockForTargetTime = 12,

    /// <summary>Consumes a frame without drawing it.</summary>
    SkipRendering = 13,
}

/// <summary>Error codes returned by libmpv entry points.</summary>
/// <remarks>
/// Mirrors <c>mpv_error</c>. Only the codes the app reacts to differently are
/// named; anything else is surfaced through <c>mpv_error_string</c>.
/// </remarks>
public enum MpvError
{
    /// <summary>No error.</summary>
    Success = 0,

    /// <summary>The event queue is empty.</summary>
    EventQueueFull = -1,

    /// <summary>Allocation failed.</summary>
    NoMem = -2,

    /// <summary>The handle has not been initialized yet.</summary>
    Uninitialized = -3,

    /// <summary>An argument was not valid for this call.</summary>
    InvalidParameter = -4,

    /// <summary>No such option.</summary>
    OptionNotFound = -5,

    /// <summary>The option exists but rejected the value's type.</summary>
    OptionFormat = -6,

    /// <summary>The option exists but rejected the value.</summary>
    OptionError = -7,

    /// <summary>No such property.</summary>
    PropertyNotFound = -8,

    /// <summary>The property exists but rejected the value's type.</summary>
    PropertyFormat = -9,

    /// <summary>The property exists but is currently unavailable.</summary>
    PropertyUnavailable = -10,

    /// <summary>The property exists but could not be read or written.</summary>
    PropertyError = -11,

    /// <summary>The command failed.</summary>
    Command = -12,

    /// <summary>The file could not be loaded.</summary>
    LoadingFailed = -13,

    /// <summary>Audio output initialization failed.</summary>
    AudioOutputInitFailed = -14,

    /// <summary>Video output initialization failed.</summary>
    VideoOutputInitFailed = -15,

    /// <summary>The file had no playable streams.</summary>
    NothingToPlay = -16,

    /// <summary>The format was not recognized.</summary>
    UnknownFormat = -17,

    /// <summary>The operation is not permitted in the current state.</summary>
    Unsupported = -18,

    /// <summary>The method is not implemented by this build.</summary>
    NotImplemented = -19,

    /// <summary>An unspecified error occurred.</summary>
    Generic = -20,
}
