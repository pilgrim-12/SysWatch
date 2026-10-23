<#
  Вызывается установщиком SysWatch (от администратора) после копирования файлов.
  Регистрирует службу агента, задачу автозапуска окна в трее и закрывает папку данных.
  Повторный запуск безопасен: используется и при обновлении.
#>
param([Parameter(Mandatory)] [string]$AppDir)
$ErrorActionPreference = 'Continue'

$service = 'SysWatchAgent'
$task    = 'SysWatch'
$agent   = Join-Path $AppDir 'SysWatch.Agent.exe'
$app     = Join-Path $AppDir 'SysWatch.exe'
$data    = Join-Path $env:ProgramData 'SysWatch'

# Папка данных: только SYSTEM и администраторы (в ней история процессов и сети).
New-Item -ItemType Directory -Force -Path $data | Out-Null
icacls $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null

# Служба агента: создаём или переводим на новый путь (обновление со старой установки через скрипт).
$svc = Get-Service $service -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $service -Force -ErrorAction SilentlyContinue }
    sc.exe config $service binPath= "`"$agent`"" start= auto | Out-Null
} else {
    New-Service -Name $service -BinaryPathName "`"$agent`"" -DisplayName 'SysWatch Agent' `
        -Description 'SysWatch: collects process, network and DNS metrics (ETW). Data: C:\ProgramData\SysWatch' `
        -StartupType Automatic | Out-Null
}
sc.exe failure $service reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
Start-Service $service -ErrorAction SilentlyContinue

# Окно в трее при входе в Windows: задача с наивысшими правами, чтобы не было окна UAC.
$action    = New-ScheduledTaskAction -Execute $app -Argument '--tray' -WorkingDirectory $AppDir
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

# Старый ярлык от установки скриптом (теперь ярлыки создаёт установщик).
$oldLnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'SysWatch.lnk'
if (Test-Path $oldLnk) { Remove-Item $oldLnk -Force -ErrorAction SilentlyContinue }
