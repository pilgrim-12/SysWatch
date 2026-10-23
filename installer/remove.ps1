<# Вызывается установщиком при удалении SysWatch: останавливает окно и агента, убирает службу и задачу. #>
$ErrorActionPreference = 'Continue'
Get-Process SysWatch -ErrorAction SilentlyContinue | Stop-Process -Force
Unregister-ScheduledTask -TaskName 'SysWatch' -Confirm:$false -ErrorAction SilentlyContinue
$svc = Get-Service SysWatchAgent -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service SysWatchAgent -Force -ErrorAction SilentlyContinue }
    sc.exe delete SysWatchAgent | Out-Null
}
Start-Sleep -Seconds 2
