# Fan and thermal control: market research and plan

Research done on 2026-10-19 for turning FanKick, a one-laptop fix, into a public fan and thermal app for Windows,
macOS and Linux. Facts were checked against each project's site, repository or release notes where possible; items
marked (unverified) could not be confirmed from a primary source.

## 1. The landscape in one page

Two events reshaped this space in 2025 and 2026:

- **WinRing0 is dead.** Since March 2025 Microsoft Defender quarantines `WinRing0x64.sys` (CVE-2020-14979: any local
  user gets physical memory, port I/O and MSR access). Open Hardware Monitor, older LibreHardwareMonitor, NBFC, older
  FanControl, OpenRGB and many others used it. Microsoft's only workaround is a Defender exclusion it does not
  recommend.
- **PawnIO replaced it.** PawnIO (namazso, GPL-2.0 with a linking exception, signed) is a "scriptable" kernel driver:
  apps load small signed bytecode modules that expose narrow operations (read this port, read that MSR) instead of
  raw ring-0 access. FanControl (V238+), LibreHardwareMonitor (0.9.5, January 2026), OpenRGB 1.0 and ZenTimings moved
  to it. Vendor drivers keep having the same class of bug: ASUS `AsIO3.sys` (CVE-2025-3464), MSI `RTCore64.sys`
  (CVE-2019-16098, abused by ransomware). On macOS, the Stats app's privileged helper accepted any client
  (CVE-2025-21606).

So the safe, modern Windows path is LibreHardwareMonitorLib + PawnIO, and any new app should avoid shipping its own
ring-0 driver.

## 2. Product by product

| Product | Platforms | License / price | Latest |
|---|---|---|---|
| FanControl (Rem0o) | Windows | Closed source, free for personal non-commercial use | V284, 2026-10-18 |
| Argus Monitor | Windows | Commercial, about 15 EUR/year | 7.4.03, 2026-09-24 |
| SpeedFan | Windows | Freeware, abandoned | 4.52, 2016 |
| HWiNFO | Windows x64/ARM64 | Free non-commercial (12 h shared-memory limit), Pro paid | 8.52, 2026-08-25 |
| Open Hardware Monitor | Windows | MPL-2.0 (unverified), unmaintained | 0.9.6, 2020 |
| LibreHardwareMonitor | Windows | MPL-2.0 | 0.9.6, 2026-02-14 |
| NBFC (hirschmann) | Windows | Historically GPL-3.0, dormant | 1.6.3, 2019 |
| nbfc-linux | Linux | GPL-3.0 | 0.5.4, 2026-10-10 |
| G-Helper (seerge) | Windows, ASUS only | GPL-3.0 | 0.288, 2026-10-13 |
| Armoury Crate | Windows, ASUS only | Free, bundled | 6.x |
| MSI Afterburner + RTSS | Windows, GPUs only | Freeware | 4.6.6 |
| Macs Fan Control | macOS (+ Boot Camp) | Free, Pro one-time purchase | 1.5.22, 2026-10-13 |
| TG Pro | macOS | 10 USD one-time | 2.10x |
| fancontrol / pwmconfig (lm-sensors) | Linux | GPL | 3.6.2, 2024-01 |
| CoolerControl | Linux only | GPL-3.0+ | 5.1.0, 2026-10-17 |
| liquidctl | Linux, Windows, macOS | GPL-3.0+ (unverified) | 1.16.0, 2026-03-03 |
| OpenRGB | Windows, Linux, macOS | GPL-2.0 (unverified) | 1.1, 2026-10-16 |

### FanControl (Rem0o)
- **Detects:** everything LibreHardwareMonitorLib sees (CPU, NVIDIA/AMD/Intel GPU, Super I/O fan headers, some ECs),
  plus NvAPI/ADLX GPU fan control, plus a plugin system (HWiNFO, Corsair/NZXT AIOs, Dell, laptops).
- **Access:** LibreHardwareMonitor over PawnIO. No PawnIO: GPU and USB devices only.
- **Curves:** the reference UX. Linear, Graph (drag points), Mix (max/min/average/sum/subtract), Trigger (idle/load
  with two temperatures), Flat, Sync (follow another fan), Auto (feedback loop to hold a target temperature).
  Per curve: hysteresis and response time. Per fan: step up %, step down %, start %, stop %, offset, minimum, RPM
  calibration.
- **Profiles:** several saved configs, switchable from the command line.
- **Safety:** no documented crash fail-safe or panic temperature.
- **Overlay:** none (tray temperature icons).
- **Gap it leaves:** Windows only, closed source, non-commercial license, no performance modes, no stress test.

### Argus Monitor
- **Detects:** Intel/AMD CPUs, NVIDIA/AMD GPUs, Super I/O (ITE, Fintek, Winbond, Nuvoton), SMART, Corsair PSUs, AIOs,
  ARCTIC fan hubs.
- **Access:** its own kernel driver (hardened in 2026).
- **Curves:** fixed or curve per source, several controllers per fan with max winning, synthetic temperatures (mean,
  max, time average), hysteresis, averaging, rate limits. Up to 24 temperature and 24 fan channels.
- **Overlay:** desktop gadget and tray icons, no in-game OSD.

### SpeedFan
- Own driver, Super I/O and SMBus. Abandoned since 2016 and blind to modern chipsets. Historical only.

### HWiNFO
- **Detects:** the widest sensor coverage on Windows, including ARM64 and Snapdragon X2.
- **Access:** its own kernel driver.
- **Fan control:** a basic dialog (manual, simple sensor-linked mode) on some boards.
- **Overlay:** feeds RivaTuner (RTSS) and shared memory; other apps (FanControl, Rainmeter) read it. The free
  version stops shared memory after 12 hours.

### Open Hardware Monitor and LibreHardwareMonitor
- OHM: WinRing0, unmaintained since 2020, now quarantined by Defender.
- LHM (the maintained fork, MPL-2.0): CPU (MSR, SMU), GPUs (NvAPI, ADL, Intel), Super I/O, ASUS and CrOS ECs,
  storage, NICs, HID AIOs and hubs (NZXT, Corsair, Aquacomputer), batteries, PSUs. Targets .NET 10. Uses PawnIO
  since 0.9.5 but **does not install it**: the driver is a separate signed installer (PawnIO 2.2.0, ARM64 supported).
  Fan control through `IControl.SetSoftware(%)` / `SetDefault()`.

### NBFC and nbfc-linux
- **Approach:** laptop embedded controller (EC) registers, read and written over ACPI EC ports 0x62/0x66, driven by
  per-model config files (about 210 XML configs on Windows, 312 JSON on Linux).
- **Windows NBFC:** WinRing0, dormant since 2019.
- **nbfc-linux (active):** `ec_sys`, `acpi_ec` DKMS (works with Secure Boot), `/dev/port` or `acpi_call`; automatic
  config matching by firmware fingerprint; EC reset after a crash; critical temperature per fan; root only.

### G-Helper and Armoury Crate (ASUS)
- **G-Helper:** talks to `\\.\ATKACPI` with IOCTL `0x0022240C`, methods DEVS (set) and DSTS (get), no driver of its
  own. Fan curves are 8 temperature + 8 percent points (CPU `0x00110024`, GPU `0x00110025`, mid `0x00110032`), fan
  RPM at `0x00110013`/`0x00110014`/`0x00110031`, performance mode at `0x00120075` (Vivobook `0x00110019`).
  Silent/Balanced/Turbo modes, each paired with a Windows power mode; GPU modes (Eco/Standard/Ultimate/Optimized);
  battery charge limit; an in-game overlay (Ctrl+Shift+Alt+O).
- **Armoury Crate:** the same firmware calls plus services and the `AsIO3.sys` driver; heavy, and the subject of a
  2025 privilege escalation CVE.
- FanKick already uses the G-Helper path for the owner's laptop, and adds what G-Helper can't do there: when the EC
  can't see the CPU temperature, FanKick reads it itself and drives the fans in software.

### MSI Afterburner + RivaTuner Statistics Server
- GPU only: a software fan curve with hysteresis, five profiles, 2D/3D switching. Uses `RTCore64.sys` plus NvAPI/ADL.
- RTSS is the de facto in-game overlay standard (framerate limiter, hotkeys, shared memory).

### Macs Fan Control and TG Pro (macOS)
- **Access:** the System Management Controller (SMC) through IOKit `AppleSMC`. Reading needs no privileges; writing
  needs root, so both ship a signed privileged helper.
- **Macs Fan Control:** per fan Auto, constant RPM, or sensor-based (one sensor, min/max temperature, linear); fans go
  back to Auto when it quits.
- **TG Pro:** Auto Boost rules (sensor over a threshold, then fan speed), manual and max.
- **Apple Silicon quirks** (from the Stats source and public research), which differ by chip:

  | Chip | Mode key | Unlock | Sequence |
  |---|---|---|---|
  | M1 | `F0Md` | none | `F0Md=1`, then target `F0Tg` |
  | M3/M4 | `F0Md` | `Ftst=1` | retry `F0Md=1` for about 3 to 6 s until thermalmonitord lets go; `Ftst=0` hands back |
  | M5 | `F0md` (lowercase) | none | `F0md=1`, then `F0Tg` |

  The mode drops back to system control within seconds of the controlling process exiting, which is a built-in
  crash fail-safe.

### Linux: fancontrol, CoolerControl, liquidctl
- **fancontrol (lm-sensors):** hwmon sysfs (`pwmN`, `pwmN_enable`, `fanN_input`, `tempN_input`). Linear curves only,
  no hysteresis. Good safety model: on any signal, read error or write error it restores the original `pwm_enable`
  (or forces 100%) and exits.
- **CoolerControl:** the strongest Linux app, still Linux only. A root daemon with a web UI, REST/gRPC API and CLI.
  Backends: hwmon, liquidctl (USB AIOs), NVML, amdgpu. Graph, Mix, Overlay, Fixed and Default profiles with
  hysteresis, step size, response delay and threshold functions; Modes; alerts; hands fans back to firmware on
  shutdown.
- **liquidctl:** the open driver set for AIO pumps and fan hubs (Corsair, NZXT Kraken, Lian Li, ASUS Ryujin, Aqua
  Computer) over USB HID.

### OpenRGB
- RGB only, not fans, but its device coverage model matters: SMBus and Super I/O through PawnIO on Windows, USB HID
  everywhere, one codebase on three OSes.

### Per-OS access summary

| OS | Sensors | Fans | Privilege |
|---|---|---|---|
| Windows | LibreHardwareMonitor: MSR/SMU (CPU), NvAPI/ADL/Intel (GPU), Super I/O (board), HID (AIO), EC | Super I/O and EC via PawnIO; GPU via NvAPI/ADL; AIO via HID; ASUS laptops via ATKACPI | Admin for PawnIO and ATKACPI |
| Linux | hwmon sysfs (k10temp, coretemp, nct67xx, it87, amdgpu, nvme, asus-wmi...), NVML for NVIDIA | hwmon `pwmN` + `pwmN_enable`; amdgpu RDNA3 `gpu_od/fan_ctrl/fan_curve`; NVML `SetFanSpeed_v2`; asus-wmi custom curves | Root for writes; reads are open |
| macOS | SMC keys over IOKit (`T*` temperatures, `F*` fans, power keys) | SMC `FnMd`/`Fnmd` + `FnTg` (with `Ftst` on M3/M4) | Root (or a signed helper) for writes |

Performance modes:

| OS | What exists |
|---|---|
| Windows | Power plans (powrprof), the Windows 11 power mode slider (undocumented `PowerSetActiveOverlayScheme`: efficiency `961cc777-...`, balanced = empty GUID, performance `ded574b5-...`, only while the Balanced plan is active), CPU boost (hidden `PERFBOOSTMODE`), vendor modes (ASUS `0x00120075`) |
| Linux | power-profiles-daemon / tuned-ppd (`powerprofilesctl`), `/sys/firmware/acpi/platform_profile`, cpufreq governor, energy-performance preference, CPU boost (`cpufreq/boost` or `intel_pstate/no_turbo`) |
| macOS | `pmset` Low Power / High Power mode (`powermode` 0/1/2 on newer Macs, `lowpowermode` on older) |

## 3. What FanKick had, and what it was missing

FanKick before this work: one Windows Forms file for one laptop (ASUS G513QE). It read the CPU and NVIDIA GPU through
LibreHardwareMonitor, drove two ASUS fans through ATKACPI with one shared curve, had ASUS modes, a "Cool mode", a CPU
temperature limit (boost off), a fixed-script thermal test, an always-on overlay and a lot of model-specific
watchdog logic for that laptop's firmware bugs.

Missing for a public app:

1. **Coverage:** any number of fans and sensors, on any machine: Super I/O headers, GPUs from three vendors, AIOs,
   other laptop brands. (Had: 2 ASUS fans, 1 CPU, 1 NVIDIA GPU.)
2. **Platforms:** macOS and Linux. (Had: Windows only, hard-coded paths.)
3. **Curves:** per-fan curves, drag-to-edit points, curves on any sensor, mix curves, hysteresis per curve, step
   rates per fan, presets, import/export. (Had: one 6-point curve for both fans, edited as a list.)
4. **Modes:** OS-aware modes beyond ASUS + three hard-coded Windows plan GUIDs.
5. **Safety in general form:** panic temperature, lost-sensor fail-safe, crash fail-safe (a watchdog that restores
   fans if the app dies), stalled-fan alerts, conflict warning with other fan apps.
6. **Stress test:** configurable CPU/GPU load with live graphs and a cutoff. (Had: a fixed 14-minute script.)
7. **Overlay:** optional (off by default), movable, choosable metrics, hotkey. (Had: always on, fixed corner.)
8. **Distribution:** installers per OS from CI, a README, a license, no personal paths.

## 4. Stack decision: C# / .NET 10 + Avalonia + LibreHardwareMonitorLib

Options considered:

| Stack | Windows sensors | Cost |
|---|---|---|
| Tauri (Rust + web UI), like Voxtray | No maintained Rust library covers Super I/O, ECs and GPU fan control on Windows. Would need a .NET sidecar running LHM anyway: two runtimes, an IPC layer, two build chains. | High |
| Electron, like DeployBell | Same sidecar problem, plus 150 MB+ per app and a JS layer between the UI and every hardware call. | High |
| **.NET 10 + Avalonia** | LibreHardwareMonitorLib runs in-process: the same library FanControl is built on, with PawnIO, NvAPI, ADL, HID AIOs. | Low |

Why .NET + Avalonia wins for this app:

- **The hard platform is Windows, and its best open library is .NET.** LibreHardwareMonitorLib (MPL-2.0) is the only
  maintained open source stack with Super I/O, EC, GPU and AIO fan control, already on PawnIO, already targeting
  .NET 10. Calling it in-process means no sidecar, no IPC, and one crash domain to make safe.
- **Linux and macOS need only thin native layers,** which C# does well: hwmon is plain file reads and writes, NVML
  and IOKit (SMC) are a handful of P/Invoke calls.
- **Avalonia** (MIT) draws the same UI on Windows x64/ARM64, macOS x64/arm64 and Linux x64/arm64 with Skia, which is
  what a custom curve editor and live graphs need. It has a tray icon API.
- **FanKick is already C#,** so the proven ASUS ATKACPI code, power plan calls, GPU sleep check and OpenCL load test
  port directly.
- **.NET 10 is LTS until November 2028.** Builds are self-contained: users install nothing.

Avalonia 11.3 (maintained, current 11.3.22) is used rather than 12.x for API stability in the first release;
moving to 12 is a contained change later.

## 5. Plan (prioritised)

Shipped in small working steps, each one buildable and tested:

1. **Core engine** (done first, unit tested): sensors and fans as plain lists from any number of backends; Graph,
   Flat and Mix curves; hysteresis; step-up/step-down rates; min/max per fan; presets (Silent, Balanced,
   Performance, Full); JSON settings with import/export; safety rules (panic temperature, lost sensor to full speed
   or BIOS, write failures hand the fan back to the BIOS, stalled fan alert).
2. **Backends:** Windows (LibreHardwareMonitor: everything it sees, never wakes a sleeping laptop GPU), ASUS laptops
   on Windows (ATKACPI fans, modes, charge limit), Linux (hwmon temps/fans/pwm/power, NVML for NVIDIA, amdgpu
   RDNA3 fan curves, RAPL CPU power), macOS (SMC: every temperature key, fans with the M1/M3-M4/M5 write
   sequences), and a Demo backend for testing and screenshots.
3. **Performance modes** per OS as listed above, vendor modes where present.
4. **UI:** dashboard with every reading and every control, curve editor with draggable points, modes page, stress
   test page, settings, tray icon.
5. **Stress test:** CPU threads and GPU load through OpenCL (present with the graphics driver on all three OSes),
   duration choice, live graphs, cutoff temperature, stops on panic.
6. **Overlay:** off by default; corner, offset, opacity, metric picker; global hotkey on Windows, tray toggle
   everywhere.
7. **Crash safety:** a small guard process watches the app and, if it dies without a clean exit, sets every fan to
   full speed (default) or hands them to the BIOS.
8. **Release:** GitHub Actions on `v*` tags: Windows x64/ARM64 (installer + portable zip), macOS universal (.dmg),
   Linux x64/ARM64 (.deb + .tar.gz), attached to a GitHub Release.

Later, when there is demand:

- Laptop EC configs for non-ASUS laptops (nbfc-linux's config set is GPL-3.0; a compatible reader is possible).
- liquidctl-class USB AIO support on Linux and macOS (Windows already gets it through LibreHardwareMonitor).
- A signed macOS privileged helper and signed Windows builds (needs paid certificates).
- Trigger and Auto (target temperature) curve types, RPM calibration.
- Root daemon + user UI split on Linux (CoolerControl's model) instead of running the UI as root.

## 6. Code signing

Unsigned apps work, but Windows SmartScreen says "Windows protected your PC", the UAC prompt says "Unknown
publisher", and macOS Gatekeeper refuses to open the app until the user approves it. The README explains how to get
past each warning. Options for removing them (checked 2026-10-19):

| Option | Cost | Publisher users see | Notes |
|---|---|---|---|
| [SignPath Foundation](https://signpath.org) | Free for OSS | "SignPath Foundation" | Needs an OSI license (FanKick: MIT), no proprietary code, an actively maintained project with a release, a code signing policy on the project page, and builds signed from CI (GitHub Actions is supported). The certificate belongs to SignPath, so the publisher is not your name. |
| [Azure Artifact Signing](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) (formerly Trusted Signing) | About 9.99 USD/month | Your verified name | Individuals: USA and Canada only (organizations: also EU and UK). Identity check takes a few days. Signs from CI. |
| [Certum Open Source Code Signing](https://shop.certum.eu/code-signing.html) | Low (roughly 25 to 70 EUR/year; price not confirmed) | "Open Source Developer, your name" | Identity check; cloud signing (SimplySign) needs a phone app, which makes unattended CI signing awkward. Validity periods were shortened from March 2026. |
| Standard OV certificate (Sectigo, DigiCert, Certum...) | About 120 to 400 USD/year | Your name or company | Cloud HSM or USB token required since 2023. |
| Apple Developer ID + notarization (macOS) | 99 USD/year | Your name | The only way to remove Gatekeeper's warning. Notarization runs from CI with an App Store Connect API key. |

None of these makes SmartScreen trust a new app on day one: since 2024 even EV certificates build reputation from
downloads over time. What signing does fix at once: the publisher name in UAC and in file properties, and
"unknown publisher" warnings.

**Recommendation:**
- **Windows:** apply to SignPath Foundation first. It is free, made for this case, and FanKick already meets its
  terms (MIT license, CI builds, public releases). The catch is that the publisher reads "SignPath Foundation", not
  harshal.dev.
- If showing your own name matters more than the cost, use Certum Open Source (cheapest with your name). Azure
  Artifact Signing is the smoothest for CI, but only if you're eligible as an individual (USA or Canada) or sign as
  a registered organization.
- **macOS:** notarization needs the 99 USD/year Apple Developer Program. It's worth it only if Mac users turn out to
  matter; until then the README's right-click > Open steps cover it.
- **Wiring:** `release.yml` already signs the Windows app and installer when `WINDOWS_CERT_PFX` and
  `WINDOWS_CERT_PASSWORD` secrets exist (a PFX certificate). SignPath and Azure each have their own GitHub Action,
  which would replace that step.
