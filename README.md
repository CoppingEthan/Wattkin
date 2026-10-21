<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/branding/wattkin-logo-dark.svg">
    <img src="assets/branding/wattkin-logo-light.svg" alt="Wattkin" width="300">
  </picture>
</p>

A small tray app that replaces MSI Center on MSI laptops. It shows how many watts your laptop is using right in the taskbar, and controls the things worth controlling: charge limits, battery calibration, performance and fan modes, the keyboard light, screen brightness, the special keys and the NVIDIA GPU. No background services, no account and no telemetry.

| | MSI Center | Wattkin |
| --- | --- | --- |
| Processes running | 8 | 1 |
| Memory used | 675 MB | 20 MB |
| Installed size | 260 MB | 1 MB |

<sub>Measured on the same MSI Prestige 16 Studio: MSI Center with its background services running, Wattkin with just its tray icon (about 0.1% CPU). Memory is private memory. Sizes leave out MSI Center's Microsoft Store app and the .NET runtime Wattkin uses.</sub>

<p align="center">
  <img src="docs/images/flyout-dark.png" alt="Wattkin flyout, dark theme" width="380">
  &nbsp;
  <img src="docs/images/flyout-light.png" alt="Wattkin flyout, light theme" width="380">
</p>

The tray icon *is* the reading:

<p align="center">
  <img src="docs/images/tray-dark.png" alt="Tray icons on a dark taskbar" height="72">
  &nbsp;
  <img src="docs/images/tray-light.png" alt="Tray icons on a light taskbar" height="72">
</p>

## Install

1. Download **Wattkin.exe** from the [latest release](https://github.com/CoppingEthan/Wattkin/releases/latest).
2. Run it and click **Install**.

Wattkin installs itself, starts with Windows and keeps itself up to date. It needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), which Windows offers to download if it's missing.

To uninstall, go to **Settings › Apps › Installed apps › Wattkin**. Your Windows power settings are put back and the battery charges to 100% again.

### MSI Center

You don't need MSI Center, and you never need to install it. If it's already on your laptop, uninstall it once Wattkin is set up: **MSI Center** in **Settings › Apps**, along with **MSI NBFoundation Service** and **MSI Center SDK** if they're listed, then restart. Running both makes them fight over the same settings.

## Features

- **Watts at a glance** - What the whole laptop is drawing right now, in the tray icon, a flyout and an optional desktop widget, with the battery time left.
- **Charge limits** - Stop charging anywhere from 10% to 100%, charge to 100% once for a long day away, or bring the battery down to the limit while plugged in.
- **Battery health** - Capacity, health and history, plus MSI's calibration for when the battery gauge drifts.
- **Profiles** - CPU power (including MSI's Sport mode where the laptop has it), fans, Windows power mode, turbo, maximum state and frequency, performance or efficiency, refresh rate and keyboard light, each set separately or left as it is. Profiles switch by themselves when you plug in or unplug, or when the battery runs low. On the Prestige 16 Studio, Quiet keeps the fans off until the CPU reaches 60°C.
- **Fan profiles** - Make named fan curves, each fan's speed at each of the laptop's temperature steps, and pick one in any profile. On laptops where Wattkin writes the whole fan table (the Stealth 15M A11UEK), the temperatures too. Fans can also be set to full speed, or off (experimental). Safety rules keep the fans ahead of the heat unless you accept the experimental mode, if you'd rather let the laptop run hot and throttle.
- **Screen brightness** - Alt+Page Down and Alt+Page Up, or a slider in the flyout. Below the lowest brightness it keeps going with an extra dim, like f.lux, for working in the dark.
- **Monitoring** - CPU, Intel graphics and RTX load and watts, temperatures, fans, RAM and disk.
- **Keyboard and keys** - Backlight brightness and auto-off, and the colour on RGB keyboards lit by Mystic Light. The mic-mute key, the mute lights and the MSI keys keep working without MSI Center.
- **NVIDIA GPU** - See whether the RTX is asleep or working, and switch between Hybrid and Integrated-only.
- **Simple and Expert settings** - One simple page for everyday use, and an Expert view with every control and a Debug tab for fan tests and diagnostics.

<p align="center">
  <img src="docs/images/settings-simple-dark.png" alt="Settings, simple view" width="640">
</p>

<details>
<summary>More screenshots</summary>

| Simple view (light) | Desktop widget |
| --- | --- |
| ![Settings, simple view, light theme](docs/images/settings-simple-light.png) | ![Desktop widget](docs/images/widget-dark.png) |

| Expert view | |
| --- | --- |
| ![Battery, expert view](docs/images/settings-expert-battery-light.png) | ![Profiles, expert view](docs/images/settings-expert-profiles-dark.png) |
| ![Graphics, expert view](docs/images/settings-expert-graphics-dark.png) | ![System details](docs/images/settings-expert-system-light.png) |
| ![Fan profiles](docs/images/settings-expert-fans-dark.png) | ![Debug tab](docs/images/settings-expert-debug-dark.png) |
</details>

## Supported laptops

| Laptops | What works |
| --- | --- |
| MSI Prestige 16 Studio A13VE | Everything, tested on the real laptop |
| MSI Stealth 15M A11UEK (EC `1563EMS1.115`) | Everything the laptop has, with whole fan tables (temperatures too) and its Mystic Light keyboard. Passed the hardware self-test on the real laptop |
| MSI Prestige 13 AI Evo A1MG | Working, from a user's report (one fan) |
| Other recent MSI laptops, including those known to the Linux [msi-ec](https://github.com/BeardOverflow/msi-ec) project | Everything the laptop supports, after a safety check at startup |
| Older MSI laptops, the newest MSI laptops (firmware interface version 3), and other brands | Watts, battery and system readings, without hardware controls |

If you try Wattkin on another MSI laptop, please [open an issue](https://github.com/CoppingEthan/Wattkin/issues/new/choose) with a diagnostics report. When hardware controls aren't available, the flyout has a button that saves one to your desktop.

## How it works

Wattkin talks to the laptop through the same firmware interface MSI Center uses, so it needs no driver. Windows only knows that interface once it has been described to it. MSI Center does that with a file of its own, while Wattkin brings its own description, so it works on a Windows install that has never had MSI software. It only changes hardware settings on firmware it recognises, and it logs every change.

Nothing leaves your laptop apart from the daily update check. Settings are kept in `%APPDATA%\Wattkin` and logs in `%LOCALAPPDATA%\Wattkin`.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). In PowerShell:

```powershell
git clone https://github.com/CoppingEthan/Wattkin.git
cd Wattkin
powershell -ExecutionPolicy Bypass -File .\build.ps1     # runs the tests, then builds .\dist\Wattkin.exe
powershell -ExecutionPolicy Bypass -File .\install.ps1   # installs your build
```

`-ExecutionPolicy Bypass` is only needed where Windows blocks scripts, and applies to that run only. If you publish a fork, set `WattkinUpdates` to `false` in `src\Wattkin\Wattkin.csproj`, so this repository's releases don't replace your builds.

`powershell -ExecutionPolicy Bypass -File .\tools\selftest.ps1` checks a build against the laptop: read-only checks first, then (after a prompt) tests that change each setting and put it back, then the power each performance mode allows under load. Close Wattkin first. Reports are saved in `%LOCALAPPDATA%\Wattkin\selftest`.

## Tech Stack

- **App:** C# on .NET 10, WPF with the Windows 11 theme
- **Hardware:** MSI's firmware interface (WMI), Windows battery and performance counters
- **Dependencies:** One (System.Management)
- **Tests:** xUnit

## Why I Built This

MSI Center does a handful of genuinely useful things, like charge limits, fan and performance modes and the GPU switch. They're buried under hardware monitors, gaming modes and support pages, running as several background processes, and its presets bundle settings together. I wanted just the useful parts, each on its own switch, in something light enough to forget it's running.

## Development & AI Usage

This project was built with AI coding agents. I came up with the idea, the features and the branding, and tested every hardware feature on my own laptop. The agents worked out how MSI Center talks to the firmware (from MSI Center's own logs and modules on my installed copy, cross-checked with the open-source projects below), wrote the code, and reviewed it for bugs in several rounds. Hardware writes were only switched on once they'd been checked on the real laptop.

## Credits

The hardware knowledge builds on [msi-ec](https://github.com/BeardOverflow/msi-ec), the Linux `msi-wmi-platform` driver documentation, [MControlCenter](https://github.com/dmitry-s93/MControlCenter), [MsiGT](https://github.com/tim-rue/MsiGT), [YAMDCC](https://codeberg.org/Sparronator9999/YAMDCC) (whole fan tables) and [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (the Mystic Light keyboard). MSI Center's behaviour was studied from the copy installed on my own laptop, for interoperability, with [ILSpy](https://github.com/icsharpcode/ILSpy) used to read its .NET modules.

Stealth 15M support, whole fan tables, the Mystic Light keyboard and the hardware self-test come from Romain's fork, [Balmor40/Wattkin](https://github.com/Balmor40/Wattkin).

Wattkin is not affiliated with or endorsed by MSI.

## License

Copyright © 2026 Ethan Copping. Wattkin is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License, version 3 or later](LICENSE). It comes with no warranty.

## Author

Ethan Copping
