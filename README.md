# Gear Hub (Central de Periféricos)

One small Windows app to set up peripherals of different brands, instead of keeping Razer Synapse, Logitech G HUB,
Corsair iCUE and DSX open just to change a setting. It talks to the devices directly where it can, and through the
vendor's own service where the hardware leaves no other way.

![](icone/icone.png)

## What it does

| Device | What you can change | How |
|---|---|---|
| Razer Huntsman V3 HE TKL | Snap Tap (on/off, pairs, mode), actuation point and rapid trigger per key, polling rate up to 8 kHz, brightness | USB feature reports, the protocol Synapse 4 uses |
| Logitech mice and headsets (HID++ 2.0) | DPI with presets, report rate, onboard memory, lighting per zone, sidetone, 10-band equalizer, battery | HID++ straight to the device; with G HUB open, DPI, sidetone and onboard mode go through G HUB's local agent so it does not undo them |
| Corsair RGB memory | Fixed colour, breathing, rainbow, or back to the module's own effect | The CORSAIR Device Control Service (the one iCUE itself uses); the iCUE window can stay closed |
| DSX | Adaptive triggers, light bar, mic light | DSX's mod server (UDP on 127.0.0.1) |
| DualSense / DualSense Edge (DSX closed) | Adaptive triggers, light bar, player LEDs, battery | HID output reports over USB or Bluetooth |
| Any webcam | Zoom, focus, exposure, white balance, brightness and the other camera controls, with auto | DirectShow camera controls (the camera light does not turn on) |
| Windows Dynamic Lighting devices | Colour and brightness | `Windows.Devices.Lights.LampArray` |
| Game controllers | Battery | `Windows.Gaming.Input` |

**Profiles** keep how every device is set. Tie games to a profile (`cs2.exe`) and it is applied when the game comes to
the front; leaving it brings the default profile back. Only the name of the foreground `.exe` is read, nothing is
opened inside the game.

No kernel driver, no test mode, nothing in ring 0. Nothing touches firmware, memory timings or voltages.

## Use

- Opens in the tray (it starts with Windows once installed). Click the icon to open the window; closing the window
  leaves it in the tray. Right click: open, switch profile, quit.
- Pick a device on the left. Changes go to the device as soon as you release a slider or pick an option.
- With a vendor app open, the hub still works, but that app may write its own settings back when it switches profile.
- RAM: with iCUE open, the first time iCUE asks whether another app may drive the memory. Accept it there, or set
  "CORSAIR RGB DRAM Service Access" to On in iCUE's memory settings.

Settings, profiles and the log live in `%USERPROFILE%\.gear-hub`.

## Build

Windows 11, .NET 10 SDK.

```powershell
powershell -ExecutionPolicy Bypass -File .\ferramentas\compilar.ps1
```

`ferramentas\instalar.ps1` installs it to `%LOCALAPPDATA%\Programs\GearHub` with a Start menu entry; it also comes
inside the [Desktop Glow](https://github.com/FelipeWASD83/desktop-glow) setup.

Command line: `--tray` (start hidden), `--quit`, `--self-test <file>` (packet building against known values),
`--probe <file>` (read-only tour of the devices), `--screenshot <file> [page]`.

## License

MIT.
