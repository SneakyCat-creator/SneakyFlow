param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$OutputDirectory = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path $PSScriptRoot -Parent)
[xml]$project = Get-Content SneakyFlow.csproj
$releaseVersion = [string]$project.Project.PropertyGroup.Version
$licenseVersion = [string]$project.Project.PropertyGroup.LicenseVersion
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$' -or $Tag -cnotmatch ('^v' + [regex]::Escape($releaseVersion) + '(-rc[1-9][0-9]*)?$')) { throw 'Tag/version mismatch.' }
$packageVersion = $Tag.Substring(1)
$sourceRoot = git rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -or [IO.Path]::GetFullPath($sourceRoot) -ne (Get-Location).Path) { throw 'Package from the SneakyFlow Git repository root.' }
$commit = git rev-parse --verify HEAD 2>$null
if ($LASTEXITCODE) { throw 'A source commit is required before packaging a release.' }
if (git status --porcelain) { throw 'Commit source changes before packaging a release.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1) { throw 'Output directory must be empty; choose a new directory to avoid mixing releases.' }
}
dotnet publish SneakyFlow.csproj -c Release --no-restore -o $output "-p:Version=$packageVersion" -p:UseSharedCompilation=false
if ($LASTEXITCODE) { throw 'Publish failed.' }
$exe = Get-Item -LiteralPath ([IO.Path]::Combine($output, 'SneakyFlow.exe'))
if ($exe.VersionInfo.ProductVersion.Split('+')[0] -cne $packageVersion) { throw 'EXE version differs from requested package version.' }
$hash = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "SHA-256 SneakyFlow.exe: $hash"
Write-Output "Created $Tag in $output. This script does not push tags or publish releases."
