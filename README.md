# Paddock

Desktop pads for Windows: portal pads, rule pads, vault pads, note pads, tabs, profiles, command palette.
A clean-room replacement for Stardock Fences (WPF, .NET 10).

## Install (no administrator needed)
Download `Paddock-Setup-<version>.exe` from Releases and run it. It installs for your user only,
to `%LocalAppData%\Programs\Paddock`. No .NET install required. Prefer no installer? Unzip the
`-portable.zip` anywhere and run `Paddock.exe`. Settings live in `%AppData%\Paddock`.

## Build
```powershell
.\build-release.ps1 -Version 0.1.0   # needs the .NET 10 SDK and Inno Setup 6
```
Outputs go to `dist\`.
