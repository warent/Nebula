#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nebula;

/// <summary>
/// Resolves <c>[DllImport("enet")]</c> against the native ENet library vendored
/// under <c>addons/Nebula/native</c> and copied next to this assembly at build time.
/// </summary>
/// <remarks>
/// Godot loads game assemblies into a load context whose <c>AssemblyDependencyResolver</c>
/// resolves native libraries from the deps.json file alone, and does not probe beside the
/// assembly. The old ENet-CSharp NuGet package shipped its natives as RID-specific assets, so
/// they were listed there; vendored copies are plain build output and are not, whichever
/// directory layout they are copied into. Without this resolver every ENet P/Invoke fails
/// with <c>DllNotFoundException</c> under Godot.
/// </remarks>
internal static class ENetNativeResolver
{
    private const string LibraryName = "enet";

    private static bool _installed;

    // CA2255 warns off module initializers in libraries. Nebula is compiled into the game
    // assembly rather than shipped as one, and registering here is what guarantees the resolver
    // is in place before any ENet P/Invoke, not just the ones on the NetRunner startup path.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(global::ENet.Library).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered for this assembly, so there is nothing to add.
            // Swallowed deliberately: this runs as a module initializer, where an escaping
            // exception fails the load of the entire Nebula assembly instead of just this
            // lookup. The worst case without it is the DllNotFoundException we would have
            // had anyway, raised at the call site where it is far easier to read.
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // ENet.cs imports "__Internal" on iOS, where the library is linked statically. Anything
        // that is not our own name falls through to the runtime's ordinary probing.
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        foreach (var directory in new[] { Path.GetDirectoryName(assembly.Location), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            foreach (var fileName in FileNames())
            {
                var path = Path.Combine(directory, fileName);

                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The names Nebula.props copies the vendored natives to, per platform.
    /// </summary>
    private static string[] FileNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new[] { "enet.dll" };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new[] { "enet.dylib", "libenet.dylib" };
        }

        return new[] { "libenet.so", "enet.so" };
    }
}
