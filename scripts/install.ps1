<#
  Ставит SysWatch целиком одной командой:
    1. агент  — служба Windows SysWatchAgent, стартует вместе с системой;
    2. окно   — SysWatch.exe, при входе в Windows запускается в трей (без запроса UAC);
    3. ярлык  — «SysWatch» в меню Пуск.
  Повторный запуск обновляет установку после изменений в коде.
#>
# Нет прав администратора — перезапускаем себя с запросом UAC.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argsLine = ($MyInvocation.BoundParameters.Keys | ForEach-Object { "-$_" }) -join ' '
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`" $argsLine"
    exit
}
$ErrorActionPreference = 'Stop'

$root     = Split-Path $PSScriptRoot -Parent
$dest     = Join-Path $env:ProgramFiles 'SysWatch'
$agentDir = Join-Path $dest 'Agent'
$appDir   = Join-Path $dest 'App'
$data     = Join-Path $env:ProgramData 'SysWatch'
$service  = 'SysWatchAgent'
$task     = 'SysWatch'
$appExe   = Join-Path $appDir 'SysWatch.exe'

function Step($text) { Write-Host "`n▸ $text" -ForegroundColor Cyan }

Step 'Останавливаю старую версию'
Get-Process SysWatch -ErrorAction SilentlyContinue | Stop-Process -Force
$svc = Get-Service $service -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service $service -Force }

Step 'Собираю агента'
dotnet publish (Join-Path $root 'src\SysWatch.Agent\SysWatch.Agent.csproj') -c Release -r win-x64 --self-contained false -o $agentDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Сборка агента не удалась' }

Step 'Собираю окно'
dotnet publish (Join-Path $root 'src\SysWatch.App\SysWatch.App.csproj') -c Release -r win-x64 --self-contained false -o $appDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Сборка окна не удалась' }

Step 'Папка данных (доступ только SYSTEM и администраторам)'
New-Item -ItemType Directory -Force -Path $data | Out-Null
icacls $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null

Step 'Служба агента'
if (-not $svc) {
    New-Service -Name $service `
        -BinaryPathName "`"$agentDir\SysWatch.Agent.exe`"" `
        -DisplayName 'SysWatch Agent' `
        -Description 'Сбор метрик процессов, сети и DNS (ETW). Данные: C:\ProgramData\SysWatch' `
        -StartupType Automatic | Out-Null
    sc.exe failure $service reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
}
Start-Service $service

Step 'Автозапуск окна в трее при входе в Windows'
# Задача планировщика с наивысшими правами: окно стартует от администратора без окна UAC.
$action    = New-ScheduledTaskAction -Execute $appExe -Argument '--tray'
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

Step 'Ярлык в меню Пуск'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'SysWatch.lnk'
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $appExe
$sc.WorkingDirectory = $appDir
$sc.Description = 'Монитор системы SysWatch'
$sc.IconLocation = "$appExe,0"
$sc.Save()

Step 'Запускаю'
Start-ScheduledTask -TaskName $task

Write-Host "`nГотово. Иконка SysWatch появилась в трее (если не видна — она под стрелкой ^ рядом с часами)." -ForegroundColor Green
Write-Host "Агент: служба '$service', данные в $data\hot"
