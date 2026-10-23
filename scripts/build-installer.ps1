<#
  Собирает установщик SysWatch: installer\Output\SysWatch-Setup-<версия>.exe
  1. публикует агента и окно self-contained в одну папку publish\app (на целевом ПК .NET не нужен);
  2. собирает установщик Inno Setup (если его нет — ставит через winget).
  Версия берётся из Directory.Build.props.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root 'publish\app'

function Step($text) { Write-Host "`n▸ $text" -ForegroundColor Cyan }

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
if (-not $version) { throw 'Нет <Version> в Directory.Build.props' }

Step "Проверяю интерфейс (wwwroot)"
$www = Join-Path $root 'src\SysWatch.App\wwwroot'
$refs = [regex]::Matches((Get-Content (Join-Path $www 'index.html') -Raw), 'assets/(index-[^"]+)') | ForEach-Object { $_.Groups[1].Value }
foreach ($r in $refs) { if (-not (Test-Path (Join-Path $www "assets\$r"))) { throw "Нет файла интерфейса $r" } }
Get-ChildItem (Join-Path $www 'assets\index-*') | Where-Object { $refs -notcontains $_.Name } | Remove-Item -Force

Step "Публикую SysWatch $version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
foreach ($proj in 'src\SysWatch.Agent\SysWatch.Agent.csproj', 'src\SysWatch.App\SysWatch.App.csproj') {
    dotnet publish (Join-Path $root $proj) -c Release -r win-x64 --self-contained true -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Сборка $proj не удалась" }
}
Remove-Item (Join-Path $out '*.pdb') -ErrorAction SilentlyContinue

Step 'Ищу Inno Setup'
$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Host 'Inno Setup не найден — ставлю через winget (один раз)...'
    winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements
    $iscc = @((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup не установился. Поставьте вручную: https://jrsoftware.org/isdl.php' }
}

Step 'Собираю установщик'
& $iscc "/DAppVersion=$version" "/DPublishDir=$out" (Join-Path $root 'installer\SysWatch.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup не собрал установщик' }

$setup = Join-Path $root "installer\Output\SysWatch-Setup-$version.exe"
Write-Host "`nГотово: $setup" -ForegroundColor Green
Write-Host ("Размер: {0:N1} МБ" -f ((Get-Item $setup).Length / 1MB))
