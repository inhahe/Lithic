# Timeline of volume-snapshot / low-capacity events, to see whether a drive
# really did run out of space (and for how long) behind a "destination full"
# warning that looks false by the time the user reads it.
$since = (Get-Date).AddHours(-30)
Get-WinEvent -FilterHashtable @{LogName = 'System'; StartTime = $since } -ErrorAction SilentlyContinue |
Where-Object { $_.ProviderName -match 'volsnap|VSS|Ntfs|disk' } |
Sort-Object TimeCreated |
ForEach-Object {
    $m = $_.Message -replace '\s+', ' '
    '{0}  {1}/{2}  {3}' -f $_.TimeCreated, $_.ProviderName, $_.Id,
    $m.Substring(0, [Math]::Min(200, $m.Length))
}
'--- done ---'
