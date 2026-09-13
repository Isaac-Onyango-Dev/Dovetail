# Dovetail

**Fits your pad to the game.**

Dovetail makes a generic USB gamepad work in games that only accept an Xbox controller.

Plenty of inexpensive pads speak DirectInput and nothing else. A modern game looks for XInput,
finds nothing, and tells you no controller is connected. Dovetail reads the pad's raw HID
reports directly, translates them, and presents a virtual Xbox 360 controller that the game
detects as the real thing — no wrapper, no launch options, no per-game DLL.

---

## What it does

| | |
|---|---|
| **Detection and translation** | Reads raw HID reports and drives a virtual Xbox 360 pad through ViGEmBus. Two pads become two independent XInput players. |
| **Guided calibration** | A wizard measures rest noise, stick range, triggers and the L3/R3 isolation test, then writes a profile per player slot. |
| **Per-game profiles** | A lens over a calibration, never an edit of it. Deadzone, saturation and response curve per game; the measured centre is never touched. |
| **Stick feel presets** | Standard, Precise, Responsive and Full range, with anti-deadzone, curve and saturation adjustable by hand. A running engine reloads within a second. |
| **Tray app and notifications** | Live controller status, per-controller naming, re-calibrate, forget, and dependency repair from the notification area. |
| **Automatic dependency install** | Detects, fetches, verifies and installs ViGEmBus and HidHide from the vendor's own release assets, with a pinned known-good build as an offline fallback. |
| **Reversible uninstall** | Removes only what Dovetail added — its own allow-list entries, its own registry value, its own profiles if you say so. Every run writes a restore point. |

---

## Install

Download **`DovetailSetup-<version>.exe`** from the
[latest release](https://github.com/Isaac-Onyango-Dev/Dovetail/releases/latest) and run it.

That is the whole procedure. It installs to Program Files, adds a Start Menu entry and an
Add/Remove Programs entry, and offers to start Dovetail when it finishes. There is no runtime
to install first and nothing to unzip or keep track of — the .NET runtime is bundled.

First run walks you through the two drivers and then calibrating your pad.

A portable `.zip` is also attached to each release for anyone who would rather not install
anything. If you use it, keep all three executables side by side: `Dovetail.exe` launches
`dovetail-diag.exe` for calibration and `dovetail-engine.exe` for dependency work, and looks
for them next to itself.

### Requirements

- Windows 10 or 11, x64. Nothing else.
- **ViGEmBus** — the virtual controller driver. Without it there is no device to present.
- **HidHide** — *required, not optional.* Without it the physical pad stays visible on
  DirectInput, and a game that polls both will bind to the raw pad and ignore Dovetail
  entirely while the engine is verifiably translating. This was found the hard way on the
  first real game tested.

Dovetail installs both drivers for you on first run. It runs as a normal user; only driver
setup requests elevation, and only for its own process.

### Uninstall

Add/Remove Programs, or the Start Menu shortcut. The uninstaller reverses what Dovetail did to
the machine — its auto-start entry, its own HidHide allow-list entries, and un-hiding any
device Dovetail hid — and **keeps your calibration**, which lives in `%LOCALAPPDATA%\Dovetail`
and costs a full input sweep per pad to recreate. ViGEmBus and HidHide are left installed.

---

## Components

| Executable | What it is |
|---|---|
| `Dovetail.exe` | The tray application. Status, settings, calibration, per-game profiles. |
| `dovetail-diag.exe` | Diagnostics and the calibration wizard: `dump`, `watch`, `sweep`, `calibrate`, `identify`. |
| `dovetail-engine.exe` | The translation engine and the command-line surface: `run`, `validate`, `slots`, `deps`, `uninstall`, `selftest`, `stage5`. |

Run either console tool with `--help` for its full usage.

---

## Building from source

```powershell
git clone https://github.com/Isaac-Onyango-Dev/Dovetail.git
cd Dovetail
dotnet build src/Dovetail.sln -c Release
```

To stage a complete, runnable folder the way it is installed:

```powershell
.\build-dist.ps1                       # -> dist\Dovetail  (self-contained, ~147 MB)
.\build-dist.ps1 -FrameworkDependent   # -> ~2.7 MB, needs the .NET 8 runtime present
```

To build the installer, with [Inno Setup 6](https://jrsoftware.org/isdl.php) installed:

```powershell
ISCC.exe /DAppVersion=1.1.0 packaging\Dovetail.iss   # -> dist\DovetailSetup-1.1.0.exe
```

CI does both on every tag and attaches the results to the release. The tag and the `<Version>`
in the projects must agree or the workflow fails, so the version in Add/Remove Programs can
never disagree with the version on the release page.

### Tests

Two hardware-free suites, 252 checks in total, both run on every push and pull request:

```powershell
dovetail-engine selftest --profile tests\fixtures\profiles   # 84  - decoding and axis scaling
dovetail-engine stage5   --dir     tests\fixtures\profiles   # 168 - slot identity, naming,
                                                             #       forgetting, per-game
                                                             #       overrides, first-run
                                                             #       gating, auto-start,
                                                             #       uninstall, HidHide
                                                             #       ownership
```

`tests/fixtures/profiles` holds a synthetic calibration pair with placeholder device identity
and nominal 8-bit ranges. It exists so the suites run on a machine that has never had a
controller calibrated; it is never shipped as a first-run seed.

The checks that genuinely need hardware — a real fresh-state dependency install, the HidHide
round trip, and in-game validation — are run by hand and recorded, because there is no honest
way to fake them on a machine that already has both drivers.

---

## Hardware validated

Developed and tested against a twin-port USB receiver reporting `VID 0x0810 / PID 0x0001`
(Shanwan "Twin USB Joystick"), with two pads driving two independent XInput players.

The translation layer is not specific to that device: calibration measures whatever byte and
bit each physical input actually moves, so any HID gamepad that reports through a standard
descriptor should work once calibrated. Only that receiver has been verified end to end.

### Game validation

| Game | Status |
|---|---|
| Sekiro: Shadows Die Twice | Validated. Detection, translation and two-player independence confirmed in-game. |
| Call of Duty: Black Ops III | **Untested — pending install.** Not a known failure; the title was not present on the test machine and its validation pass has not been run. |

---

## Profiles

Calibration lives in `%LOCALAPPDATA%\Dovetail\profiles` as `slot1.calibration.json`,
`slot2.calibration.json` — one per player, not one per port. Identity is by player slot, so the
first pad to connect becomes player 1 regardless of which socket it is in.

A pad with no calibration gets **no virtual device**. That is deliberate: guessing another
unit's numbers produces a controller that works but feels wrong, which is far harder to
diagnose in-game than one that plainly does not appear.

`dovetail-engine deps paths` prints which folder the running build is using and why.

---

## Uninstall from the command line

Add/Remove Programs calls the first of these for you. Run them directly to see what an
uninstall would touch before committing to it, or to reverse one afterwards.

```powershell
dovetail-engine uninstall plan      # what would be removed, changing nothing
dovetail-engine uninstall run       # remove it, writing a restore point first
dovetail-engine uninstall restore   # put it all back
```

ViGEmBus and HidHide are left installed — they are the operator's, not Dovetail's. Devices
that Dovetail hid are un-hidden; devices hidden by you or by another application are not.
Cloaking is turned off only if Dovetail turned it on and nothing else still needs it.

---

## Licence

[MIT](LICENSE).

Dovetail depends on [ViGEmBus](https://github.com/nefarius/ViGEmBus) and
[HidHide](https://github.com/nefarius/HidHide) by Nefarius Software Solutions, which it
downloads from their own release assets and installs; it does not redistribute them.
