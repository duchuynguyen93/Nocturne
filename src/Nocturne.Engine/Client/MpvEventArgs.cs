using Nocturne.Engine.Interop;

namespace Nocturne.Engine.Client;

/// <summary>An observed libmpv property changed.</summary>
public sealed class MpvPropertyChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments for a property change.</summary>
    public MpvPropertyChangedEventArgs(string name, MpvFormat format, object? value)
    {
        Name = name;
        Format = format;
        Value = value;
    }

    /// <summary>Property name, such as <c>time-pos</c>.</summary>
    public string Name { get; }

    /// <summary>Shape the value was delivered in.</summary>
    public MpvFormat Format { get; }

    /// <summary>
    /// The new value, or <see langword="null"/> when the property currently has
    /// none — the normal state of <c>time-pos</c> between files.
    /// </summary>
    public object? Value { get; }

    /// <summary>Reads the value as a double, or null when it is not one.</summary>
    public double? AsDouble => Value as double?;

    /// <summary>Reads the value as a flag, or null when it is not one.</summary>
    public bool? AsBoolean => Value as bool?;

    /// <summary>Reads the value as a string, or null when it is not one.</summary>
    public string? AsString => Value as string;
}

/// <summary>A playlist entry stopped.</summary>
public sealed class MpvEndFileEventArgs : EventArgs
{
    /// <summary>Creates the arguments for an end-of-file event.</summary>
    public MpvEndFileEventArgs(MpvEndFileReason reason, MpvError error)
    {
        Reason = reason;
        Error = error;
    }

    /// <summary>Why the entry stopped.</summary>
    public MpvEndFileReason Reason { get; }

    /// <summary>
    /// The failure code when <see cref="Reason"/> is
    /// <see cref="MpvEndFileReason.Error"/>, otherwise
    /// <see cref="MpvError.Success"/>.
    /// </summary>
    public MpvError Error { get; }

    /// <summary>
    /// Whether the entry finished normally and the playlist should advance.
    /// </summary>
    /// <remarks>
    /// Only <see cref="MpvEndFileReason.Eof"/> means "advance". A stop or a quit
    /// is the app's own doing and advancing on it produces the bug where closing
    /// a file starts the next one.
    /// </remarks>
    public bool ReachedEnd => Reason == MpvEndFileReason.Eof;
}

/// <summary>A log line from libmpv.</summary>
public sealed class MpvLogEventArgs : EventArgs
{
    /// <summary>Creates the arguments for a log line.</summary>
    public MpvLogEventArgs(string prefix, string level, string text)
    {
        Prefix = prefix;
        Level = level;
        Text = text;
    }

    /// <summary>Subsystem that emitted the line, such as <c>vo/gpu</c>.</summary>
    public string Prefix { get; }

    /// <summary>libmpv level name, such as <c>warn</c>.</summary>
    public string Level { get; }

    /// <summary>The message, with its trailing newline removed.</summary>
    public string Text { get; }

    /// <inheritdoc />
    public override string ToString() => $"[{Level}] {Prefix}: {Text}";
}
