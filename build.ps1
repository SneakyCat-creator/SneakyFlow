param([switch]$SkipTests, [switch]$NoPublish)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:APPDATA = Join-Path $PSScriptRoot '.dotnet\AppData'
$env:LOCALAPPDATA = Join-Path $PSScriptRoot '.dotnet\LocalAppData'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
dotnet restore SneakyFlow.csproj --configfile NuGet.Config --locked-mode
if ($LASTEXITCODE) { throw 'Не удалось восстановить зависимости.' }
dotnet build SneakyFlow.csproj -c Release --no-restore
if ($LASTEXITCODE) { throw 'Ошибка сборки.' }
if (-not $SkipTests) {
    dotnet run --project tests/SneakyFlow.Smoke/SneakyFlow.Smoke.csproj -c Release
    if ($LASTEXITCODE) { throw 'Проверки не пройдены.' }
    dotnet run --project tests/SneakyFlow.UiSmoke/SneakyFlow.UiSmoke.csproj -c Release -- artifacts/ui-check
    if ($LASTEXITCODE) { throw 'Проверка тёмного оформления не пройдена.' }
}
if ($NoPublish) { Write-Host 'Исходники собраны и проверены. Портативный EXE не формировался.'; return }
dotnet publish SneakyFlow.csproj -c Release --no-restore -o artifacts/portable
if ($LASTEXITCODE) { throw 'Ошибка упаковки.' }
Get-FileHash artifacts/portable/SneakyFlow.exe -Algorithm SHA256
Write-Host 'Готово: artifacts\portable\SneakyFlow.exe'
