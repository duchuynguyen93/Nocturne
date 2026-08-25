using System.Runtime.InteropServices;
using System.Text;

namespace Nocturne.Engine.Interop;

/// <summary>
/// Converts between managed strings and the NUL-terminated UTF-8 libmpv expects.
/// </summary>
/// <remarks>
/// libmpv treats every string as UTF-8 on every platform. The default
/// marshaller for <see cref="string"/> uses ANSI on Windows, so relying on it
/// works for ASCII paths and then fails on the first accented or CJK file name
/// with an error that reads as "file not found" for a file that is plainly
/// there. Doing the encoding explicitly makes that class of bug impossible.
/// </remarks>
internal static unsafe class Utf8
{
    /// <summary>
    /// Copies a managed string into native memory as NUL-terminated UTF-8.
    /// </summary>
    /// <returns>
    /// A buffer the caller must dispose. <see langword="null"/> input yields a
    /// null pointer, which libmpv reads as "no value" for options.
    /// </returns>
    internal static Utf8Buffer Allocate(string? value) => new(value);

    /// <summary>
    /// Reads a NUL-terminated UTF-8 string that libmpv owns.
    /// </summary>
    /// <remarks>Does not free the pointer; the caller decides that.</remarks>
    internal static string? Read(nint pointer) =>
        pointer == nint.Zero ? null : Marshal.PtrToStringUTF8(pointer);

    /// <summary>
    /// Reads a string libmpv allocated and releases it in the same step.
    /// </summary>
    /// <remarks>
    /// <c>mpv_get_property_string</c> and friends hand back memory that must go
    /// back through <c>mpv_free</c> rather than the CLR allocator. Pairing the
    /// read and the free here is what keeps every call site from leaking.
    /// </remarks>
    internal static string? ReadAndFree(nint pointer)
    {
        if (pointer == nint.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            MpvNative.Free(pointer);
        }
    }
}

/// <summary>
/// A NUL-terminated UTF-8 copy of a managed string, in native memory.
/// </summary>
internal readonly unsafe struct Utf8Buffer : IDisposable
{
    private readonly nint _pointer;

    internal Utf8Buffer(string? value)
    {
        if (value is null)
        {
            _pointer = nint.Zero;
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        _pointer = Marshal.AllocHGlobal(byteCount + 1);
        var destination = new Span<byte>((void*)_pointer, byteCount + 1);
        Encoding.UTF8.GetBytes(value, destination);
        destination[byteCount] = 0;
    }

    /// <summary>Pointer to the encoded bytes, or null for a null input.</summary>
    internal byte* Pointer => (byte*)_pointer;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_pointer != nint.Zero)
        {
            Marshal.FreeHGlobal(_pointer);
        }
    }
}
