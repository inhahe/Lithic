<#
Bound (and optionally relocate) a volume's shadow-copy diff area.

Why this exists: `vssadmin add shadowstorage` is a Windows *Server* command. On
client SKUs vssadmin offers only Delete/List/Resize, and `resize` cannot create an
association that doesn't exist yet -- so on Windows 11 there is no vssadmin route
to "give D: a 100 GB diff area on E:". The underlying capability is still there,
though: `Win32_ShadowStorage.Create(Volume, DiffVolume, MaxSpace)` is the WMI
method vssadmin wraps, and it is present on client Windows.

What this buys you: a diff area grows with every block overwritten on the volume
while a shadow copy is live, and if it is unbounded a leaked snapshot can consume
the entire volume. Capping it turns "the disk filled up" into "the snapshot got
aborted". Redirecting it onto a roomier volume takes the protected volume's free
space out of the equation altogether.

Usage (from an ELEVATED prompt):
    .\vss-limit.ps1 -For D: -On E: -MaxSizeGB 100     # redirect and cap
    .\vss-limit.ps1 -For D: -MaxSizeGB 20             # cap in place
    .\vss-limit.ps1                                   # just report

Notes:
  * Creating or moving an association DELETES existing shadow copies of -For.
  * -On must be NTFS, and -For must not be the system volume if -On differs
    (Windows pins the system volume's diff area to itself for System Restore).
  * Redirected traffic is real: copy-on-write for -For then lands on -On.
#>
[CmdletBinding()]
param(
    [string] $For,
    [string] $On,
    [double] $MaxSizeGB
)

function Format-Max([uint64] $bytes) {
    if ($bytes -ge [uint64]::MaxValue) { 'UNBOUNDED' } else { '{0:N1} GB' -f ($bytes / 1GB) }
}

function Show-State([string] $header) {
    '', "=== $header ==="
    $stores = @(Get-CimInstance Win32_ShadowStorage -ErrorAction SilentlyContinue)
    if (-not $stores) {
        '  (no diff area allocated on any volume -- every volume is effectively UNBOUNDED)'
    }
    foreach ($s in $stores) {
        # Volume/DiffVolume are references; resolve them to drive letters.
        $vol = (Get-CimInstance -InputObject $s | Select-Object -ExpandProperty Volume)
        $diff = (Get-CimInstance -InputObject $s | Select-Object -ExpandProperty DiffVolume)
        '  for={0} on={1} used={2:N2} GB allocated={3:N2} GB max={4}' -f
        $vol.DeviceID, $diff.DeviceID, ($s.UsedSpace / 1GB), ($s.AllocatedSpace / 1GB),
        (Format-Max $s.MaxSpace)
    }
    $copies = @(Get-CimInstance Win32_ShadowCopy -ErrorAction SilentlyContinue)
    "  shadow copies present: $($copies.Count)"
}

$elevated = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Show-State 'current'

if (-not $For) { '', 'No -For given; reporting only.'; return }

if (-not $elevated) {
    '', 'ERROR: creating or resizing a diff area needs an elevated prompt.'
    'Re-run this script from an Administrator PowerShell.'
    exit 1
}

$forVol = $For.TrimEnd('\') + '\'
$onVol = if ($On) { $On.TrimEnd('\') + '\' } else { $forVol }
if (-not $MaxSizeGB) { '', 'ERROR: -MaxSizeGB is required.'; exit 1 }
$maxBytes = [uint64] ($MaxSizeGB * 1GB)

# Windows enforces a 320 MB floor; asking for less is silently rounded up, so say
# so rather than let the caller believe a smaller cap took effect.
if ($maxBytes -lt 320MB) { "  note: {0} GB is below the 320 MB floor and will be rounded up." -f $MaxSizeGB }

'', "=== creating association: for=$forVol on=$onVol max=$($MaxSizeGB) GB ==="
'  (this deletes existing shadow copies of ' + $forVol + ')'

try {
    $r = Invoke-CimMethod -ClassName Win32_ShadowStorage -MethodName Create -Arguments @{
        Volume     = $forVol
        DiffVolume = $onVol
        MaxSpace   = $maxBytes
    } -ErrorAction Stop
    if ($r.ReturnValue -eq 0) { '  OK: association created.' }
    else { "  Create returned $($r.ReturnValue) (0x{0:X8})" -f $r.ReturnValue }
}
catch {
    "  Create failed: $($_.Exception.Message)"
    # The usual cause is that an association for this volume already exists, in
    # which case resize is the right verb and IS available on client Windows.
    '  falling back to: vssadmin resize shadowstorage'
    $out = & vssadmin resize shadowstorage /for=$For /on=$(if ($On) { $On } else { $For }) /maxsize=$maxBytes 2>&1
    $out | ForEach-Object { '    ' + $_ }
}

Show-State 'after'
'--- done ---'
