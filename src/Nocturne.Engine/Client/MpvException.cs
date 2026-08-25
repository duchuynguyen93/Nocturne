using Nocturne.Engine.Interop;

namespace Nocturne.Engine.Client;

/// <summary>A libmpv call returned an error code.</summary>
public sealed class MpvException : Exception
{
    /// <summary>Creates an exception carrying a libmpv error code.</summary>
    /// <param name="error">The code libmpv returned.</param>
    /// <param name="operation">What the app was doing, for the message.</param>
    public MpvException(MpvError error, string operation)
        : base($"{operation} failed: {DescribeError(error)} ({(int)error}).")
    {
        Error = error;
        Operation = operation;
    }

    /// <summary>Creates an exception with an explicit message.</summary>
    public MpvException(string message)
        : base(message)
    {
        Error = MpvError.Generic;
        Operation = string.Empty;
    }

    /// <summary>Creates an exception wrapping an inner failure.</summary>
    public MpvException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = MpvError.Generic;
        Operation = string.Empty;
    }

    /// <summary>The libmpv error code.</summary>
    public MpvError Error { get; }

    /// <summary>What the app was attempting when the error occurred.</summary>
    public string Operation { get; }

    /// <summary>
    /// Whether this failure belongs to one file rather than to the player.
    /// </summary>
    /// <remarks>
    /// A file the app cannot open is an ordinary outcome that belongs in the
    /// UI as a message on the video surface. A handle that will not initialize
    /// is not recoverable and should reach a crash report. Treating both the
    /// same way either hides real breakage or shows a fatal dialog because
    /// someone dragged in a corrupt download.
    /// </remarks>
    public bool IsItemLevel => Error is MpvError.LoadingFailed
        or MpvError.NothingToPlay
        or MpvError.UnknownFormat
        or MpvError.Command;

    /// <summary>
    /// Turns a code into text, preferring libmpv's own description.
    /// </summary>
    /// <remarks>
    /// Falls back to the enum name when libmpv cannot be reached at all — which
    /// is exactly the case where the native library failed to load, and where a
    /// second P/Invoke to ask for the message would throw over the top of the
    /// error being reported.
    /// </remarks>
    private static string DescribeError(MpvError error)
    {
        try
        {
            return Utf8.Read(MpvNative.ErrorString((int)error)) ?? error.ToString();
        }
        catch (DllNotFoundException)
        {
            return error.ToString();
        }
        catch (EntryPointNotFoundException)
        {
            return error.ToString();
        }
    }
}
