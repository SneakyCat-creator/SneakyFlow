param([string]$TicketPath, [string]$TicketHash)
$ErrorActionPreference = 'Stop'
$operationDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($TicketPath))
$issues = New-Object 'Collections.Generic.List[string]'
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
function Assert-NoLinks([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -Force -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Symbolic link or junction: $cursor" }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Remove-ProfileTree([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ne $Root -and -not $full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Profile path escaped its root.' }
    Assert-NoLinks $full
    foreach ($entry in Get-ChildItem -Force -LiteralPath $full) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing a profile link: $($entry.FullName)" }
        if ($entry.PSIsContainer) { Remove-ProfileTree $entry.FullName $Root }
        else { Remove-Item -Force -LiteralPath $entry.FullName }
    }
    Remove-Item -Force -LiteralPath $full
}
try {
    $operationsRoot = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'SneakyFlow', 'operations').TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($operationDirectory) -ne $operationsRoot -or
        [IO.Path]::GetFileName($TicketPath) -ne 'remove.json' -or [IO.Path]::GetFileName($operationDirectory) -notmatch '^[a-f0-9]{32}$') { throw 'Invalid removal directory.' }
    Assert-NoLinks $TicketPath
    if ((Get-Sha256 $TicketPath) -ne $TicketHash) { throw 'Removal request changed.' }
    $ticket = [IO.File]::ReadAllText($TicketPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $profileRoot = [IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'SneakyFlow')
    if ([IO.Path]::GetFullPath($ticket.Profile) -ne $profileRoot) { throw 'Unexpected profile directory.' }
    Assert-NoLinks $profileRoot
    $keyHasher = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($keyHasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($profileRoot.ToUpperInvariant()))).Replace('-', '').Substring(0,20) }
    finally { $keyHasher.Dispose() }
    $leasePath = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'SneakyFlow', ('maintenance-' + $key + '.lock'))
    Assert-NoLinks $leasePath
    $lease = New-Object IO.FileStream($leasePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
    $parent = Get-Process -Id $ticket.Pid
    if ($parent.StartTime.ToUniversalTime().Ticks -ne $ticket.StartTicks -or $parent.Path -ne $ticket.Current) { throw 'Original process identity changed.' }
    Set-Content -LiteralPath ([IO.Path]::Combine($operationDirectory, 'remove-ready')) -Value 'ready'
    if (-not $parent.WaitForExit(90000)) { throw 'SneakyFlow did not exit; nothing was deleted.' }
    foreach ($copy in $ticket.Copies) {
        try {
            $copyPath = [IO.Path]::GetFullPath($copy.Path)
            if (-not (Test-Path -LiteralPath $copyPath -PathType Leaf)) { continue }
            Assert-NoLinks $copyPath
            if ([Diagnostics.FileVersionInfo]::GetVersionInfo($copyPath).OriginalFilename -ne 'SneakyFlow.dll' -or
                (Get-Sha256 $copyPath) -ne $copy.Sha256) { throw "Copy changed; kept: $copyPath" }
            Remove-Item -Force -LiteralPath $copyPath
        } catch { $issues.Add($_.Exception.Message) }
    }
    foreach ($cache in $ticket.RuntimeCaches) {
        try {
            $cacheRoot = [IO.Path]::GetFullPath($cache.Path)
            if (-not (Test-Path -LiteralPath $cacheRoot)) { continue }
            $extractRoot = [IO.Path]::Combine([IO.Path]::GetTempPath(), '.net').TrimEnd('\')
            if (-not $cacheRoot.StartsWith($extractRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($cacheRoot)) -ne $extractRoot) { throw 'Unexpected runtime cache path.' }
            $proof = [IO.Path]::Combine($cacheRoot, 'SneakyFlow.dll')
            Assert-NoLinks $proof
            if ((Get-Sha256 $proof) -ne $cache.AssemblySha256) { throw "Runtime cache changed; kept: $cacheRoot" }
            Remove-ProfileTree $cacheRoot $cacheRoot
        } catch { $issues.Add($_.Exception.Message) }
    }
    try { if (Test-Path -LiteralPath $profileRoot) { Remove-ProfileTree $profileRoot $profileRoot } }
    catch { $issues.Add($_.Exception.Message) }
    if ($issues.Count -gt 0) { throw ($issues -join [Environment]::NewLine) }
    Remove-Item -LiteralPath $TicketPath
    Remove-Item -LiteralPath ([IO.Path]::Combine($operationDirectory, 'remove-ready'))
    Remove-Item -LiteralPath $operationDirectory
} catch {
    $message = 'SneakyFlow: removal could not finish.' + [Environment]::NewLine + $_.Exception.Message
    [IO.File]::WriteAllText([IO.Path]::Combine($operationDirectory, 'remove-error.txt'), $message, [Text.Encoding]::UTF8)
    # Honest failure reporting: no forced process termination or elevation.
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($message, 'SneakyFlow') | Out-Null
    exit 1
} finally {
    if ($lease) { $lease.Dispose() }
}
