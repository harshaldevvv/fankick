using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FanKick;

// One place that maps the short library names used in [DllImport] to each OS's real file.
static class Native
{
    [ModuleInitializer]
    internal static void Init() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);

    static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        string[] candidates = name switch
        {
            "nvidia-ml" => OperatingSystem.IsWindows() ? ["nvml.dll"] : ["libnvidia-ml.so.1", "libnvidia-ml.so"],
            "OpenCL" => OperatingSystem.IsWindows() ? ["OpenCL.dll"] : OperatingSystem.IsMacOS() ? ["/System/Library/Frameworks/OpenCL.framework/OpenCL"] : ["libOpenCL.so.1", "libOpenCL.so"],
            "IOKit" => ["/System/Library/Frameworks/IOKit.framework/IOKit"],
            "CoreFoundation" => ["/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"],
            _ => [],
        };
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, out var h)) return h;
        return IntPtr.Zero; // fall back to the default search
    }
}
