# LithicBackup — project notes for Claude

## Versioning (Claude controls this)

**Current version: `1.0.53`**

Claude owns the version number. Do not hand-edit it — ask Claude to bump it and
Claude will keep every place in sync.

- **Authoritative source:** `src/Directory.Build.props` (`<Version>` element).
  Every project under `src\` inherits it, so the GUI, Worker, and all libraries
  stamp the same version. `installer\build-installer.ps1` reads it too, so the
  MSI `ProductVersion` never drifts from the assembly version.
- **This heading** (the "Current version" line above) is kept in sync by Claude
  on every bump, so you can glance here to know what to tag a release.
- The in-app update check compares this version against the latest GitHub
  release tag (`inhahe/Lithic`), so the released tag must match.

### Cutting a release

1. Claude bumps `<Version>` in `src/Directory.Build.props` (and updates the
   "Current version" line above).
2. Build the MSI: `powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1`
   (reads the version automatically). This builds the managed custom action
   (`installer\CustomActions`) and compiles `LithicBackup-<version>-x64.msi`.
3. Publish a GitHub release on `inhahe/Lithic` tagged **`v<version>`** (e.g.
   `v1.0.11`) with the `installer\LithicBackup-<version>-x64.msi` attached.

The tag must be `v` + the exact version so the update check matches it.

**How upgrades close the running GUI *and* Worker service (no elevation, no
reboot):** neither can be safely *killed* or *stopped* by the installer's
pre-validate work — everything before `InstallInitialize` runs unelevated at the
invoking user's Medium integrity, which can't terminate a High-integrity GUI
(UIPI) and can't SCM-stop a LocalSystem service. That's what caused the "The
setup was unable to automatically close all requested applications" failures
(error 1611). Instead the installer *asks* both processes to exit: the managed
custom action `SignalLithicShutdown` (`installer\CustomActions\CustomAction.cs`)
sets the named event each listens on and waits (bounded, concurrently) for both
to exit, all before `InstallValidate`:

| Process | Event | Listener |
|---|---|---|
| GUI | `LithicBackup.Shutdown` | `src\LithicBackup\App.xaml.cs` |
| Worker service | `Global\LithicBackup.Worker.Shutdown` | `src\LithicBackup.Worker\ShutdownSignalListener.cs` |

A process can always close itself regardless of integrity level, so no
`taskkill`, elevation, or self-elevating bundle is needed. (`Global\` for the
Worker because the service is in session 0 while the custom action is in the
user's interactive session.)

**Why `<ServiceControl Stop="both">` in `Package.wxs` does not cover this:** it
executes at `StopServices` (sequence **1900**), while the file-in-use check is at
`InstallValidate` (**1400**) — 500 steps earlier, with the service still running
and still holding its exe and DLLs. It's kept for what it *does* do: hold the
service down for the file transfer and unregister it on uninstall.

**Bootstrap caveat:** this only works once the *running* build already contains
the listener — the upgrade that first delivers one still falls back to Windows
Installer's own handling for that process. See the MSI-upgrade entry in
`known-issues.md`.
