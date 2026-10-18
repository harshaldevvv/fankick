# Security

FanKick runs with administrator / root rights because changing fan speeds needs them, so security reports matter.

## Reporting a vulnerability

Please report privately through GitHub: **Security > Report a vulnerability** on
https://github.com/harshaldevvv/fankick (private vulnerability reporting). Do not open a public issue for it.
You should get a reply within a week.

## Design notes

- FanKick ships no kernel driver of its own. On Windows it uses the signed PawnIO driver through
  LibreHardwareMonitorLib, and ASUS's own ATKACPI driver on ASUS laptops. It never uses WinRing0.
- It opens no network ports. Its only network request is the PawnIO download you start yourself, which is
  checked against PawnIO's published SHA-256 before it runs.
- Release builds are produced by GitHub Actions from the tagged source; checksums are attached to each release.
