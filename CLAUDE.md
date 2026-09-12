# LithicBackup — project notes for Claude

## Versioning (Claude controls this)

**Current version: `1.0.72`**

Claude owns the version number. Do not hand-edit it — ask Claude to bump it and
Claude will keep every place in sync.

**Which component to bump depends on what actually changed** — use SemVer judgement:

- **patch** (`1.0.69` → `1.0.70`) — bug fixes, internal refactors, or a plain rebuild
  with no user-visible change in behaviour.
- **minor** (`1.0.69` → `1.1.0`) — a new user-facing feature or option, or a noticeable
  change to how an existing feature works.
- **major** (`1.0.69` → `2.0.0`) — either a **breaking change** — anything that alters the
  catalog or on-disc backup format, changes the restore path, or invalidates existing
  backup sets or settings — **or a milestone overhaul**: a redesign of the main window or
  the backup/restore workflow, or a body of work large enough that you would describe the
  app as a new generation rather than an update. Backwards compatibility is not the only
  trigger; a release that changes what using LithicBackup *feels* like earns a major bump
  even when every old backup set still restores.

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

`build-msi.bat` is a convenience wrapper around step 2: it runs the same
script and then **deletes** every older `LithicBackup-*-x64.msi` from
`installer\` and the repo root, so only the just-built one is in view and a
stale installer cannot be launched by mistake. It used to *move* them into
`installer\archive\`, which quietly accumulated 57 installers / 3.2 GB before
anyone noticed; deleting is safe because every version's `<Version>` is
stamped in a committed `src/Directory.Build.props`, so any earlier installer
can be rebuilt with `git checkout <commit>` + `build-installer.ps1` (the same
build, though not the same bytes - an MSI carries a fresh package code and
timestamps each time). The one workflow that wants an older MSI is testing an
upgrade against the shutdown handshake; rebuild it from its commit for that.

The tag must be `v` + the exact version so the update check matches it.

**How upgrades close the running GUI *and* Worker service (no reboot):** neither
can be safely *killed* or *stopped* by the installer — Restart Manager, driven
from the unelevated client process, can't terminate a High-integrity GUI (UIPI),
and a `taskkill`/SCM-stop needs rights the installer shouldn't assume. That's
what caused the "The setup was unable to automatically close all requested
applications" failures (error 1611). Instead the installer *asks* both processes
to exit: the managed custom action `SignalLithicShutdown`
(`installer\CustomActions\CustomAction.cs`) sets the named event each listens on
and waits (bounded, concurrently) for both to exit, all before `InstallValidate`.
A process can always close itself regardless of integrity level, so no
`taskkill`, elevation, or self-elevating bundle is needed.

| Process | Event | Listener |
|---|---|---|
| GUI | `Global\LithicBackup.Shutdown`, falling back to `LithicBackup.Shutdown` | `src\LithicBackup\App.xaml.cs` |
| Worker service | `Global\LithicBackup.Worker.Shutdown` | `src\LithicBackup.Worker\ShutdownSignalListener.cs` |

**`installer\CustomActions\CustomAction.config` is load-bearing. Do not remove or
rename it.** `SfxCA.dll` (the native stub `MakeSfxCA` wraps around the managed
action) has to pick a CLR *before* it can load any managed code, and its only
input is a file named exactly `CustomAction.config`. Without it, SfxCA binds
**CLR v2.0.50727**, loading the `net472` assembly throws
`BadImageFormatException`, and — because the action is authored `Return="ignore"`
— Windows Installer logs "returned actual error code 1603 but will be translated
to success due to continue marking" and carries on. That is not hypothetical: it
is what happened from 1.0.11 to 1.0.55, during which `SignalLithicShutdown`
**never executed a single line** and every 1611 was misattributed to the
bootstrap caveat. The file is packed by a `<Content>` item in the `.csproj`;
losing either the item or the exact filename silently returns the action to
never running.

**Why the GUI is asked on three names.** `SignalGui` tries `Global\…`, then
`Session\<n>\…` for the session of each running GUI process, then the bare name.
The `Session\<n>\` prefix resolves against that session's object directory
whatever session the caller is in, which (a) makes the handshake work against
**older GUIs that only ever published the session-local name**, so the fix is
retroactive rather than taking effect one upgrade later, and (b) covers a
system-context deployment where the action really would run as SYSTEM in session
0. For an ordinary double-click the action runs **impersonated as the invoking
user in the user's own session** — measured, `rundll32`, session 1 — so the bare
name would do; the extra names cost microseconds and remove a whole class of
guesswork. The action logs its own account/pid/session on every run, which is
the only reason the earlier misdiagnosis was caught.

**Why `<ServiceControl Stop="both">` in `Package.wxs` does not cover this:** it
executes at `StopServices` (sequence **1900**), while the file-in-use check is at
`InstallValidate` (**1400**) — 500 steps earlier, with the service still running
and still holding its exe and DLLs. It's kept for what it *does* do: hold the
service down for the file transfer and unregister it on uninstall.

**Bootstrap caveat:** this only works once the *running* build already contains
the listener — the upgrade that first delivers one still falls back to Windows
Installer's own handling for that process. See the MSI-upgrade entry in
`known-issues.md`.
