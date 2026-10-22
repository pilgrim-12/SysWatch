# Удаляет SysWatch. История удаляется только с ключом -RemoveData.
param([switch]$RemoveData)
# Нет прав администратора — перезапускаем себя с запросом UAC.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argsLine = ($MyInvocation.BoundParameters.Keys | ForEach-Object { "-$_" }) -join ' '
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`" $argsLine"
    exit
}
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
