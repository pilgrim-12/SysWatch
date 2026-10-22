#Requires -RunAsAdministrator
# Устанавливает SysWatch Agent как службу Windows (автозапуск, перезапуск при сбое).
$ErrorActionPreference = 'Stop'

$root    = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\SysWatch.Agent\SysWatch.Agent.csproj'
$dest    = Join-Path $env:ProgramFiles 'SysWatch\Agent'
$data    = Join-Path $env:ProgramData 'SysWatch'
$name    = 'SysWatchAgent'

$existing = Get-Service $name -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Останавливаю и удаляю старую службу..."
    if ($existing.Status -ne 'Stopped') { Stop-Service $name -Force }
    sc.exe delete $name | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Сборка в $dest ..."
dotnet publish $project -c Release -r win-x64 --self-contained false -o $dest
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с ошибкой" }

Write-Host "Папка данных $data (доступ только SYSTEM и Administrators)..."
New-Item -ItemType Directory -Force -Path $data | Out-Null
icacls $data /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null

Write-Host "Регистрация службы..."
New-Service -Name $name `
    -BinaryPathName "`"$dest\SysWatch.Agent.exe`"" `
    -DisplayName 'SysWatch Agent' `
    -Description 'Сбор метрик процессов, сети и DNS (ETW). Данные: C:\ProgramData\SysWatch' `
    -StartupType Automatic | Out-Null
sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

Start-Service $name
Get-Service $name | Format-Table Name, Status, StartType -AutoSize
Write-Host "Готово. Логи: Просмотр событий -> Журналы Windows -> Приложение (источник SysWatch.Agent)."
