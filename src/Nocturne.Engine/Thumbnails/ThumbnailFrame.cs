namespace Nocturne.Engine.Thumbnails;

/// <summary>One decoded preview frame.</summary>
/// <param name="Position">The position the frame was taken from.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Pixels">
/// Pixels as BGRA, four bytes each, tightly packed at <see cref="Width"/> times
/// four bytes per row. The alpha byte is set to opaque.
/// </param>
/// <remarks>
/// <para>
/// The position is carried with the pixels because it is the only thing that
/// makes a late frame identifiable. Seeks are answered out of order under a
/// fast drag, and a preview showing the wrong moment is worse than one showing
/// nothing.
/// </para>
/// <para>
/// libmpv is asked for <c>bgr0</c> rather than <c>rgb0</c> because that is the
/// byte order <c>BGRA8</c> surfaces want on Windows, and a per-pixel swizzle on
/// the way to the screen is work for nothing. Its fourth byte is padding that
/// libmpv leaves at zero, which every consumer reads as a fully transparent
/// pixel, so it is filled in before the frame is handed over.
/// </para>
/// </remarks>
public sealed record ThumbnailFrame(TimeSpan Position, int Width, int Height, byte[] Pixels);
