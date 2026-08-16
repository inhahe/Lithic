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

'', '=== existing shadow copies ==='
Get-CimInstance Win32_ShadowCopy -ErrorAction SilentlyContinue |
Sort-Object InstallDate |
ForEach-Object { '{0}  {1}  {2}' -f $_.InstallDate, $_.VolumeName, $_.ID }

'', '=== shadow storage areas ==='
Get-CimInstance Win32_ShadowStorage -ErrorAction SilentlyContinue |
ForEach-Object {
    '{0}  used={1:N1} GB  allocated={2:N1} GB  max={3}' -f
    $_.Volume.DeviceID, ($_.UsedSpace / 1GB), ($_.AllocatedSpace / 1GB),
    $(if ($_.MaxSpace -ge [uint64]::MaxValue) { 'unbounded' } else { '{0:N1} GB' -f ($_.MaxSpace / 1GB) })
}
'--- done ---'
