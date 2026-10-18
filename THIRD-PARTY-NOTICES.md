# Third-party notices

FanKick is MIT licensed (see LICENSE). Release builds include the following components, each under its own license.
Their source code is available at the links given.

| Component | License | Source |
|---|---|---|
| .NET runtime | MIT | https://github.com/dotnet/runtime |
| Avalonia (incl. Avalonia.Native, Win32, X11, FreeDesktop, Skia, Fluent theme) | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia ANGLE natives (Windows) | BSD-3-Clause | https://github.com/AvaloniaUI/angle |
| Inter font (via Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| SkiaSharp, HarfBuzzSharp | MIT | https://github.com/mono/SkiaSharp |
| MicroCom.Runtime | MIT | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol | MIT | https://github.com/tmds/Tmds.DBus |
| LibreHardwareMonitorLib | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit, RAMSPDToolkit-NDD, BlackSharp.Core | MPL-2.0 | https://github.com/Blacktempel |
| HidSharp | Apache-2.0 | https://www.zer7.com/software/hidsharp |
| Mono.Posix.NETStandard | MIT | https://github.com/mono/mono |
| System.Management, System.IO.Ports, System.CodeDom | MIT | https://github.com/dotnet/runtime |

MPL-2.0 components are used unmodified, as separate library files; their source is at the links above.

## Not bundled

- **PawnIO** (GPL-2.0-or-later with a linking exception, https://pawnio.eu). The Windows installer can download and run
  the official signed PawnIO installer from https://github.com/namazso/PawnIO.Setup if you tick that option. FanKick
  talks to it only through LibreHardwareMonitorLib.
- GPU vendor libraries (NVIDIA NVML / NvAPI, AMD ADL, OpenCL) come with your graphics driver and are loaded at run time.

## Trademarks

ASUS, ROG, Armoury Crate, NVIDIA, GeForce, AMD, Radeon, Ryzen, Intel, Apple, Mac, macOS, Windows and other names are
trademarks of their owners. They are used only to describe compatible hardware. FanKick is an independent project and
is not affiliated with or endorsed by any of them.
