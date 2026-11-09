# tiny11-minimal

Automated, reproducible **Tiny11** build using the **official Tiny11 Builder engine**
([ntdevlabs/tiny11builder](https://github.com/ntdevlabs/tiny11builder), `Tiny11Builder.Core`) on a
GitHub Actions Windows runner. `harness/` is a small headless console front end for that engine;
the WinUI app itself is not used.

Each run:

1. Downloads the **latest official Windows 11 ISO** from Microsoft (x64, en-US) through the builder's own downloader.
2. Downloads the pinned **Dell Inspiron 15-3567** drivers (Microsoft Update Catalog, `drivers/manifest.json`) and injects them into the image, so every device works offline right after setup.
3. Builds a **Custom** Tiny11 from `recipe/tiny11-minimal.json`, plus exact advanced removals
   selected from the image inventory by `recipe/advanced-removals.json`.
4. Publishes the ISO as a GitHub Release, split into parts under 2 GB.

## What this build keeps

Windows Update, Defender / Windows Security, WinRE (recovery), Settings, File Explorer, Terminal,
Notepad, Calculator, Snipping Tool, Photos, winget (App Installer) and .NET 3.5 (enabled).

## What it removes

Microsoft Store, Edge and WebView2, OneDrive, Copilot, Recall and AI apps, Widgets, Teams, Outlook/Mail,
Xbox and Game Bar, Clipchamp, News/Weather/Bing, Office promos, Solitaire, Phone Link, Feedback/Tips,
Dev Home, Power Automate, People/To Do/Family/Skype, Maps, Media Player/Movies, Clock/Recorder/Sticky
Notes, Quick Assist, Paint/3D Viewer/OneNote, Camera, OEM promos, speech/OCR/handwriting/IME payloads,
WordPad/Steps Recorder/legacy WMP, XPS, Work Folders, IE, Hello Face, PowerShell ISE, Fax & Scan,
VBScript, Braille, Mixed Reality, extra wallpapers, sounds and help, and printer/scanner driver payloads.

Policies: no ads or suggestions, minimal telemetry, no web search, no Copilot/Recall/Widgets, OOBE nags off,
local account setup, hardware checks bypassed (TPM/CPU/RAM/Secure Boot), reserved storage off,
**automatic BitLocker device encryption off**, Compact OS. Services disabled: telemetry, Xbox, maps,
retail demo, geolocation, error reporting, parental controls, biometrics, Mixed Reality and similar.

## Rebuild

Edit `recipe/*.json` (or touch `BUILD_TRIGGER`) and push to `main`, or run the workflow manually.
After a run, `reports.zip` in the release contains `inventory.tsv` (every removable item in the image),
`effective-recipe.json`, `report.json` and the build/DISM logs.

## Reassemble

```sh
cat tiny11-*.iso.part* > tiny11.iso && sha256sum -c SHA256SUMS.txt
```
