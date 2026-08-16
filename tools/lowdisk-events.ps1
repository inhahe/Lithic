# Who is making shadow copies, and did a volume run out of space?
#
# Windows keeps no free-space history, but it does log the two things that
# matter when free space craters and recovers on its own: Volsnap's diff-area
# growth failures (Volsnap 24 / 35) and the VSS requester activity that caused
# the snapshot to exist in the first place.
$since = (Get-Date).AddHours(-30)

# Anchored, and deliberately NOT a bare 'Backup': the Worker registers itself as
# provider 'LithicBackup.Worker', so an unanchored match buries the handful of
# Volsnap lines that matter under hundreds of our own per-file log entries.
$providers = '^(volsnap|VSS|Ntfs|disk|Microsoft-Windows-Backup|SPP|srv|' +
             'Microsoft-Windows-VolumeSnapshot|System Restore)'

'=== volsnap / VSS / Ntfs / disk events ==='
foreach ($log in 'System', 'Application') {
    Get-WinEvent -FilterHashtable @{LogName = $log; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -match $providers } |
    Sort-Object TimeCreated |
    ForEach-Object {
        $m = $_.Message -replace '\s+', ' '
        '{0}  [{1}] {2}/{3}  {4}' -f $_.TimeCreated, $log, $_.ProviderName, $_.Id,
        $m.Substring(0, [Math]::Min(220, $m.Length))
    }
}

# A shadow copy is meant to be short-lived: a backup opens one, reads the frozen
# volume, and releases it. One that is still around hours later is almost always
# leaked, and it is silently eating the volume the whole time -- every block
# overwritten while it lives has to be copied into the diff area. This is the
# check worth running *before* the volume fills, not after.
$STALE_HOURS = 2

# Both VSS classes throw "Initialization failure" for a non-admin instead of
# returning nothing, so a swallowed error would print a confident "(none)" that
# means "you are not allowed to look". Distinguish the two.
function Get-VssClass([string] $class) {
    try { return @{ Ok = $true; Items = @(Get-CimInstance $class -ErrorAction Stop) } }
    catch { return @{ Ok = $false; Error = $_.Exception.Message } }
}

'', '=== existing shadow copies ==='
$q = Get-VssClass 'Win32_ShadowCopy'
if (-not $q.Ok) { "  CANNOT READ ($($q.Error)) -- run this script elevated" }
elseif ($q.Items.Count -eq 0) { '  (none)' }
$copies = if ($q.Ok) { $q.Items } else { @() }
foreach ($c in ($copies | Sort-Object InstallDate)) {
    $age = (Get-Date) - $c.InstallDate
    $warn = if ($age.TotalHours -ge $STALE_HOURS) { '  <-- STALE, likely leaked' } else { '' }
    '{0}  age {1,5:N1} h  {2}  {3}{4}' -f $c.InstallDate, $age.TotalHours,
    $c.VolumeName, $c.ID, $warn
}

'', '=== shadow storage areas ==='
$qs = Get-VssClass 'Win32_ShadowStorage'
if (-not $qs.Ok) { "  CANNOT READ ($($qs.Error)) -- run this script elevated" }
elseif ($qs.Items.Count -eq 0) { '  (none -- no diff area allocated on any volume)' }
$stores = if ($qs.Ok) { $qs.Items } else { @() }
foreach ($s in $stores) {
    # An unbounded max is what lets a leaked snapshot consume the entire volume;
    # capping it turns "the disk filled up" into "the snapshot got aborted".
    $max = if ($s.MaxSpace -ge [uint64]::MaxValue) { 'UNBOUNDED <-- cap this' }
    else { '{0:N1} GB' -f ($s.MaxSpace / 1GB) }
    '{0}  used={1:N1} GB  allocated={2:N1} GB  max={3}' -f
    $s.Volume.DeviceID, ($s.UsedSpace / 1GB), ($s.AllocatedSpace / 1GB), $max
}

# CrashPlan leaks snapshots when its own 60 s open timeout races the watchdog
# thread that completes the open: the requester gives up and never calls close,
# but the shadow copy exists anyway. Opens minus closes is the leak count.
'', '=== CrashPlan VSS open/close balance ==='
$cpLog = 'C:\ProgramData\CrashPlan\log'
if (Test-Path $cpLog) {
    foreach ($f in Get-ChildItem "$cpLog\service.log.*" -ErrorAction SilentlyContinue) {
        $text = Select-String -Path $f.FullName -Pattern 'OPEN VSS|CLOSE VSS|VSS open TIMED OUT' -ErrorAction SilentlyContinue
        $opens = @($text | Where-Object { $_.Line -match 'OPEN VSS' }).Count
        $closes = @($text | Where-Object { $_.Line -match 'CLOSE VSS' }).Count
        $timeouts = @($text | Where-Object { $_.Line -match 'TIMED OUT' }).Count
        '{0,-16} opens={1,-4} closes={2,-4} timeouts={3,-4} leaked={4}' -f
        $f.Name, $opens, $closes, $timeouts, ($opens - $closes)
    }
}
else { '  (CrashPlan not installed)' }
'--- done ---'
