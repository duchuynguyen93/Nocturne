using System.Reflection;

namespace Nocturne.Engine.Interop;

/// <summary>
/// Entry point for assemblies that P/Invoke libmpv directly.
/// </summary>
/// <remarks>
/// <para>
/// libmpv is loaded through a <c>DllImportResolver</c> that maps the logical
/// name <c>mpv</c> onto the real per-platform file name — <c>libmpv-2.dll</c> on
/// Windows. <see cref="System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver"/>
/// is scoped to a single assembly.
/// </para>
/// <para>
/// That scoping is a trap. An assembly that declares its own
/// <c>[DllImport("mpv")]</c> and never registers gets the default probing
/// order, which looks for a bare <c>mpv.dll</c> — a file that does not exist in
/// any libmpv distribution. The result is a <c>DllNotFoundException</c> on the
/// first call, from code that looks identical to code that works.
/// </para>
/// </remarks>
public static class MpvRuntime
{
    /// <summary>
    /// Registers <paramref name="assembly"/> so its <c>mpv</c> imports resolve.
    /// </summary>
    /// <remarks>Call before that assembly makes its first libmpv call.</remarks>
    public static void RegisterInteropAssembly(Assembly assembly) =>
        MpvNative.RegisterCallingAssembly(assembly);

    /// <summary>The libmpv client API version, as major and minor.</summary>
    public static (int Major, int Minor) ApiVersion
    {
        get
        {
            MpvNative.EnsureResolverRegistered();
            ulong version = MpvNative.ClientApiVersion();
            return ((int)(version >> 16), (int)(version & 0xFFFF));
        }
    }
}
