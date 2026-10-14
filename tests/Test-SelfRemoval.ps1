$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$runRoot = [IO.Path]::Combine($workspace, 'artifacts', 'removal-test-' + [Guid]::NewGuid().ToString('N'))
if (-not $runRoot.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test root.' }
$profile = [IO.Path]::Combine($runRoot, 'SneakyFlow')
New-Item -ItemType Directory -Path ([IO.Path]::Combine($profile, 'SneakyFlow.Data', 'metadata')) -Force | Out-Null
Set-Content -LiteralPath ([IO.Path]::Combine($profile, 'product-state.json')) -Value 'fixture'
Set-Content -LiteralPath ([IO.Path]::Combine($profile, 'SneakyFlow.Data', 'metadata', 'fixture.torrent')) -Value 'fixture'
$outside = [IO.Path]::Combine($runRoot, 'unrelated-download.txt')
Set-Content -LiteralPath $outside -Value 'must survive'
$fixtureDirectory = [IO.Path]::Combine($workspace, 'tests', 'RemovalFixture', 'bin', 'Release', 'net10.0')
$first = [IO.Path]::Combine($fixtureDirectory, 'Disposable-' + [Guid]::NewGuid().ToString('N') + '.exe')
$second = [IO.Path]::Combine($runRoot, 'SecondCopy.exe')
Copy-Item -LiteralPath ([IO.Path]::Combine($fixtureDirectory, 'SneakyFlow.exe')) -Destination $first
Copy-Item -LiteralPath $first -Destination $second
$parent = Start-Process -FilePath $first -PassThru -WindowStyle Hidden
$cache = [IO.Path]::Combine([IO.Path]::GetTempPath(), '.net', 'SneakyFlow-Test-' + [Guid]::NewGuid().ToString('N'), 'fixture-cache')
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$cacheProof = [IO.Path]::Combine($cache, 'SneakyFlow.dll')
Copy-Item -LiteralPath ([IO.Path]::Combine($fixtureDirectory, 'SneakyFlow.dll')) -Destination $cacheProof
Set-Content -LiteralPath ([IO.Path]::Combine($cache, 'runtime.fixture')) -Value 'extracted runtime fixture'
$operation = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'SneakyFlow', 'operations', [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $operation -Force | Out-Null
$ticketPath = [IO.Path]::Combine($operation, 'remove.json')
@{ Pid=$parent.Id; StartTicks=$parent.StartTime.ToUniversalTime().Ticks; Current=$first; Profile=$profile;
   Copies=@(@{Path=$first; Sha256=(Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash},
            @{Path=$second; Sha256=(Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash});
   RuntimeCaches=@(@{Path=$cache; AssemblySha256=(Get-FileHash -LiteralPath $cacheProof -Algorithm SHA256).Hash}) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ticketPath -Encoding utf8
$hash = (Get-FileHash -LiteralPath $ticketPath -Algorithm SHA256).Hash
$script = Get-Content -Raw -LiteralPath ([IO.Path]::Combine($workspace, 'Services', 'SelfRemove.ps1'))
# Substitute ONLY the profile base for this test. The production helper has no test bypass parameter.
$needle = "[Environment]::GetFolderPath('LocalApplicationData')"
if (-not $script.Contains($needle)) { throw 'Test substitution failed.' }
$script = $script.Replace($needle, "'$($runRoot.Replace("'", "''"))'")
# Fail the test instead of showing a modal error; successful production flow is unchanged.
$script = $script.Replace('[System.Windows.MessageBox]::Show($message, ''SneakyFlow'') | Out-Null', 'Write-Error $message')
$command = "& {`n$script`n} '$($ticketPath.Replace("'", "''"))' '$hash'"
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = [IO.Path]::Combine([Environment]::GetFolderPath('System'), 'WindowsPowerShell', 'v1.0', 'powershell.exe')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.ArgumentList.Add('-NoProfile'); $start.ArgumentList.Add('-NonInteractive'); $start.ArgumentList.Add('-Command'); $start.ArgumentList.Add($command)
$helper = [Diagnostics.Process]::Start($start)
if (-not $helper.WaitForExit(30000)) { throw 'Removal helper timed out.' }
if ($helper.ExitCode -ne 0) { throw (Get-Content -Raw -LiteralPath ([IO.Path]::Combine($operation, 'remove-error.txt'))) }
if ((Test-Path -LiteralPath $profile) -or (Test-Path -LiteralPath $first) -or (Test-Path -LiteralPath $second) -or (Test-Path -LiteralPath $operation) -or (Test-Path -LiteralPath $cache)) { throw 'Removal left fixture files behind.' }
if ((Get-Content -Raw -LiteralPath $outside).Trim() -ne 'must survive') { throw 'Unrelated files changed.' }
Write-Output 'PASS isolated PowerShell self-removal: waited for original process, removed two registered copies, profile, known extracted runtime and handoff files; preserved unrelated download'
