# LithicBackup — project notes for Claude

## Versioning (Claude controls this)

**Current version: `1.0.12`**

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

**How upgrades close the running GUI (no elevation, no reboot):** the GUI can't
be safely *killed* by the installer when it runs elevated (a Medium-integrity
installer action can't terminate a High-integrity process — that's what caused
the old "The setup was unable to automatically close all requested applications"
failures). Instead the installer *asks* the GUI to exit: the managed custom
action `SignalLithicGuiShutdown` (`installer\CustomActions\CustomAction.cs`) sets
a named event the GUI listens on (`App.xaml.cs`, `LithicBackup.Shutdown`) and
waits for `LithicBackup.exe` to exit, all before `InstallValidate`. A process can
always close itself regardless of integrity level, so no `taskkill`, elevation,
or self-elevating bundle is needed. **Bootstrap caveat:** this only works once the
*running* build already contains the listener — the upgrade that first delivers
it still relies on the in-app updater closing the old GUI itself (the custom
action's wait removes the race) or on the user closing it manually. See the
MSI-upgrade entry in `known-issues.md`.
