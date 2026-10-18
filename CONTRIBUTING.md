# Contributing to FanKick

Thanks for helping. FanKick is MIT licensed; by sending a pull request you agree your contribution is released under
the same license.

## Reporting a problem

Open an issue with:

- your OS and version, and your machine (laptop model, or motherboard and GPU);
- what you expected and what happened;
- `FanKick.log` from the settings folder (Settings > Files > Open settings folder). It holds sensor and fan
  events only, no personal data, but read it before posting.

Security issues: please follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Hardware support

The most useful contribution is a report from hardware we haven't seen: what the Dashboard shows, which fans appear,
and whether setting them works. If a fan is missing, include the output of:

- Windows: the Dashboard's sensor list (a screenshot is fine)
- Linux: `ls /sys/class/hwmon/*/` and `cat /sys/class/hwmon/*/name`
- macOS: the Dashboard's sensor list

## Code

Needs the .NET 10 SDK.

```bash
dotnet test tests/FanKick.Tests
dotnet run --project src/FanKick -- --demo
```

- `--demo` runs on simulated hardware, so UI work needs no real fans and touches nothing.
- Keep changes small and focused, match the surrounding style, and add a test in `tests/FanKick.Tests` for any
  change to the curve or safety logic in `src/FanKick/Core`.
- Hardware code lives in `src/FanKick/Hardware`, one file per backend. A new backend implements `IHardware`
  (sensors, fans, `Set`, `Restore`) and is added in `Backends.Create`.
- Safety comes first: a change must never leave a fan at a fixed low speed when the app stops, loses a sensor or
  fails a write.

CI builds every target (Windows, macOS, Linux; x64 and ARM64) on each pull request.
