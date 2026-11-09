# Drivers: Dell Inspiron 15-3567 (system ID 078B)

`devices.json` lists every device in this laptop by hardware ID (taken from the running machine).
`manifest.json` pins, for each device, the newest Windows 10/11 x64 driver package in the
**Microsoft Update Catalog** (the same WHQL packages Windows Update serves), with exact cab URLs.
The build downloads those cabs and injects every INF into the Windows 11 Pro image, so all devices
work offline straight after setup.

| Device | Package |
|---|---|
| Intel HD Graphics 620 | Intel Display 31.0.101.2135 (2025) |
| AMD Radeon R5 M430 | AMD Display 27.20.20904.4000 (Dell subsystem) |
| Intel Wireless-AC 3165 | Intel net 19.51.50.2 |
| Intel Bluetooth | Intel Bluetooth 23.40.0.2 |
| Realtek RTL8101 Ethernet | Realtek 10.74.1128.2024 (Dell subsystem) |
| Realtek ALC3246 audio | Realtek MEDIA 6.0.10029.1 |
| Intel display (HDMI) audio | Intel MEDIA 10.27.0.9 |
| Intel Serial IO I2C #0/#1 + GPIO (touchpad bus) | Intel System 30.100.2351.81 |
| Intel Management Engine | Intel 2603.9.4.0 |
| Chipset: SMBus, PMC, host bridge, thermal | Intel chipset INFs |
| Intel Dynamic Tuning / processor thermal | Intel 8.7.10802 / 8.7.10700 |
| Intel HID Event Filter (Fn/brightness keys) | Intel 2.2.2.18 |
| Intel watchdog timer | Intel 11.7.0.1019 |
| Realtek RTS5129 card reader | Realtek USB 10.0.26100.31289 |

The touchpad (I2C HID) and webcam (USB UVC) use Windows' in-box Precision Touchpad and UVC drivers.

Refresh the pins:

```sh
python3 -I scripts/resolve_drivers.py resolve drivers/devices.json drivers/manifest.json
```

Any extra driver folders committed here (subdirectories containing `.inf`) are injected too.
