# File Organizer

A lightweight **Windows 10/11 x64** background application that watches multiple user-selected directories and moves incoming files using independent, ordered, TOML-configured rules. No GUI, server, cloud service, or database is required. A future optional editor will only read/write the configuration file.

## Intended behavior
For a monitored folder `%USERPROFILE%\Downloads`:

| Files | Destination relative to Downloads |
|---|---|
| `.ase`, `.aseprite` | `Images\Aseprite` |
| `.png`, `.jpg`, `.jpeg`, `.tiff`, `.gif`, `.bmp` | `Images` |
| name matching `*invoice*.pdf` or `*fattura*.pdf` | `Documents\Invoices` |
| `.zip`, `.7z`, `.rar` | `Archives` |

The first matching active rule wins. Unmatched files remain untouched. Destinations may also be absolute paths, including on other volumes. A second watched directory can have entirely different rules.

## CLI contract

```powershell
FileOrganizer.exe run                  # watch continuously (default command)
FileOrganizer.exe scan                 # process one snapshot, then exit
FileOrganizer.exe validate             # parse and check config, no modifications
FileOrganizer.exe install              # register per-user automatic login startup
FileOrganizer.exe uninstall            # remove ONLY this app's startup entry
FileOrganizer.exe --help
```

All commands support `--config <path>`; otherwise load the per-user application config from `%LOCALAPPDATA%\FileOrganizer\config.toml`. `install` must register an absolute executable path and the explicit config path, without admin elevation. `uninstall` never removes user files or config. `scan` and `run` honor `dry_run`.

## Configuration
Start from [`Config.Example.toml`](Config.Example.toml). Copy it to `%LOCALAPPDATA%\FileOrganizer\config.toml`, then edit directory paths and rules. Do not automatically enable live file movement on first run if the config file is absent: show an actionable error; `validate` should also fail with a clear message.

The machine-readable schema, rules and policies are specified in [`REQUIREMENTS.md`](REQUIREMENTS.md); processing flow and safe transfers in [`FUNCTIONAL.md`](FUNCTIONAL.md); verification in [`TEST.md`](TEST.md). These are design specifications until Codex implements them, **not claims that an executable already exists**.

## Deployment goals
- One background process for all configured directories; no tray icon or UI.
- Event-driven watching, bounded background work and near-idle CPU use.
- Native AOT Windows x64 Release if supported, self-contained standard .NET fallback otherwise.
- No regular-use administrator privileges; no Windows Service required.
- Logs are bounded and stored under `%LOCALAPPDATA%\FileOrganizer\logs`.

## Safety defaults
No silent overwrite, no automatic deletion, no processing of incomplete downloads, no source/destination identity, no movement under invalid configuration. File content hashing occurs on relevant same-name collisions only.
