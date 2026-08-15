<#
.SYNOPSIS
    Dumps a built MSI's InstallExecuteSequence and CustomAction tables.

.DESCRIPTION
    Diagnostic for the sequencing invariant that governs the "setup was unable to
    automatically close all requested applications" dialog (error 1611):

        SignalLithicShutdown  MUST come before  InstallValidate

    InstallValidate (1400) is where Windows Installer runs its file-in-use check.
    Anything that still holds a file in the install folder at that point produces
    the dialog. StopServices (1900) — where <ServiceControl Stop> executes — is
    500 steps too late to help, which is why the custom action exists. Use this
    script after changing installer\Package.wxs to confirm the ordering survived.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\dump-seq.ps1 `
        -Msi installer\LithicBackup-1.0.53-x64.msi
#>
param([Parameter(Mandatory = $true)][string]$Msi)

$Msi = (Resolve-Path $Msi).Path

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Msi, 0))

function Get-MsiRows([string]$sql) {
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
    # [void] matters: InvokeMember returns null here, and an unsuppressed null
    # would be emitted as the function's first "row".
    [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    while ($true) {
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $rec) { break }
        , $rec
    }
}

function Get-Str($rec, [int]$field) {
    $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, @($field))
}
function Get-Int($rec, [int]$field) {
    $rec.GetType().InvokeMember('IntegerData', 'GetProperty', $null, $rec, @($field))
}

Write-Host "=== $([System.IO.Path]::GetFileName($Msi)) ==="
Write-Host ""
Write-Host "--- InstallExecuteSequence (1300..2000) ---"
foreach ($r in Get-MsiRows 'SELECT Action, Sequence FROM InstallExecuteSequence WHERE Sequence > 1300 AND Sequence < 2000 ORDER BY Sequence') {
    '{0,6}  {1}' -f (Get-Int $r 2), (Get-Str $r 1)
}

Write-Host ""
Write-Host "--- CustomAction ---"
foreach ($r in Get-MsiRows 'SELECT Action, Type, Source, Target FROM CustomAction') {
    '{0,-28} type={1,-5} source={2,-22} target={3}' -f (Get-Str $r 1), (Get-Str $r 2), (Get-Str $r 3), (Get-Str $r 4)
}
