#Requires -RunAsAdministrator
# Удаляет SysWatch. История удаляется только с ключом -RemoveData.
param([switch]$RemoveData)
$ErrorActionPreference = 'Continue'

Get-Process SysWatch -ErrorAction SilentlyContinue | Stop-Process -Force
Unregister-ScheduledTask -TaskName 'SysWatch' -Confirm:$false -ErrorAction SilentlyContinue

$svc = Get-Service SysWatchAgent -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service SysWatchAgent -Force }
    sc.exe delete SysWatchAgent | Out-Null
}

Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'SysWatch.lnk') -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Remove-Item (Join-Path $env:ProgramFiles 'SysWatch') -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    Remove-Item (Join-Path $env:ProgramData 'SysWatch') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'История удалена.'
}
Write-Host 'SysWatch удалён.'
