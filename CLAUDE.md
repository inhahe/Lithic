# LithicBackup — project notes for Claude

## Versioning (Claude controls this)

**Current version: `1.0.5`**

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
   (reads the version automatically).
3. Publish a GitHub release on `inhahe/Lithic` tagged **`v<version>`** (e.g.
   `v1.0.3`) with the `installer\LithicBackup-<version>-x64.msi` attached.

The tag must be `v` + the exact version so the update check matches it.
