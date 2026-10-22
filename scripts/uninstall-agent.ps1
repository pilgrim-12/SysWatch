#Requires -RunAsAdministrator
# Удаляет службу SysWatch Agent. Данные удаляются только с ключом -RemoveData.
param([switch]$RemoveData)
$ErrorActionPreference = 'Stop'
$name = 'SysWatchAgent'

$svc = Get-Service $name -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $name -Force }
    sc.exe delete $name | Out-Null
    Write-Host "Служба удалена."
}

$dest = Join-Path $env:ProgramFiles 'SysWatch'
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force; Write-Host "Удалено: $dest" }

if ($RemoveData) {
    $data = Join-Path $env:ProgramData 'SysWatch'
    if (Test-Path $data) { Remove-Item $data -Recurse -Force; Write-Host "Удалены данные: $data" }
}
