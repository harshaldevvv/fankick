# Privacy

FanKick collects nothing and sends nothing.

- No telemetry, analytics, crash reporting, accounts or ads.
- It makes no network connections, with one exception you start yourself: the **Install PawnIO** button (and the
  matching option in the Windows installer) downloads that driver's official installer from GitHub. Nothing about you
  is sent.
- Everything it stores stays on your machine, in its settings folder:
  - Windows: `%APPDATA%\FanKick`
  - macOS: `~/Library/Application Support/FanKick`
  - Linux: `~/.config/FanKick`
- That folder holds `settings.json` (your curves and options) and `FanKick.log` (sensor and fan events, capped at
  1 MB). Delete it to reset FanKick.
