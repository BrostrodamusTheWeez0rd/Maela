# Fetches build-time dependencies from their official sources and verifies every file
# against the SHA-256 hashes pinned in deps\*.lock. Nothing third-party is stored in
# this repository; a file that doesn't match its pinned hash stops the build.
#
# Works with the Windows PowerShell 5.1 that ships with Windows 10/11.
# Output: obj\deps\<name>\...  (only the files listed in the lock, nothing else)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Get-NormalizedSha256([string]$path) {
    # Hash with CRLF normalised to LF, so the result doesn't depend on how the
    # archive or a git checkout chose to store line endings.
    $bytes = [IO.File]::ReadAllBytes($path)
    $out = New-Object IO.MemoryStream ($bytes.Length)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 13 -and ($i + 1) -lt $bytes.Length -and $bytes[$i + 1] -eq 10) { continue }
        $out.WriteByte($bytes[$i])
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($out.ToArray()) } finally { $sha.Dispose() }
    return ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

function Read-Lock([string]$lockPath) {
    $lock = @{ Commit = $null; Url = $null; Files = New-Object 'System.Collections.Generic.List[object]' }
    foreach ($line in [IO.File]::ReadAllLines($lockPath)) {
        $t = $line.Trim()
        if ($t.Length -eq 0 -or $t.StartsWith('#')) { continue }
        if ($t.StartsWith('commit ')) { $lock.Commit = $t.Substring(7).Trim(); continue }
        if ($t.StartsWith('url ')) { $lock.Url = $t.Substring(4).Trim(); continue }
        $parts = $t -split '\s+', 2
        if ($parts.Length -ne 2 -or $parts[0] -notmatch '^[0-9a-f]{64}$') { throw "Bad line in ${lockPath}: $line" }
        $lock.Files.Add(@{ Hash = $parts[0]; Path = $parts[1] })
    }
    if (-not $lock.Url -or $lock.Files.Count -eq 0) { throw "$lockPath is incomplete." }
    if (-not $lock.Url.StartsWith('https://github.com/')) { throw "$lockPath must point at an official https://github.com source." }
    return $lock
}

function Test-Installed($lock, [string]$dest) {
    foreach ($f in $lock.Files) {
        $p = Join-Path $dest $f.Path
        if (-not (Test-Path -LiteralPath $p)) { return $false }
        if ((Get-NormalizedSha256 $p) -ne $f.Hash) { return $false }
    }
    return $true
}

function Install-Dependency([string]$name) {
    $lockPath = Join-Path $root "deps\$name.lock"
    $lock = Read-Lock $lockPath
    $dest = Join-Path $root "obj\deps\$name"

    if ((Test-Path -LiteralPath $dest) -and (Test-Installed $lock $dest)) {
        Write-Host "${name}: verified ($($lock.Files.Count) files)."
        return
    }

    Write-Host "${name}: downloading $($lock.Url)"
    $work = Join-Path $root "obj\deps\.download-$name"
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    New-Item -ItemType Directory -Path $work | Out-Null
    $zip = Join-Path $work 'source.zip'
    $client = New-Object Net.WebClient
    $client.Headers.Add('User-Agent', 'maela-build')
    $client.DownloadFile($lock.Url, $zip)

    $extract = Join-Path $work 'x'
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $top = Get-ChildItem -LiteralPath $extract -Directory | Select-Object -First 1
    if (-not $top) { throw "${name}: archive was empty." }

    # Verify everything before installing anything.
    $failures = 0
    foreach ($f in $lock.Files) {
        $src = Join-Path $top.FullName $f.Path
        if (-not (Test-Path -LiteralPath $src)) { Write-Host "  MISSING  $($f.Path)"; $failures++; continue }
        $actual = Get-NormalizedSha256 $src
        if ($actual -ne $f.Hash) { Write-Host "  MISMATCH $($f.Path)"; $failures++ }
    }
    if ($failures -gt 0) { throw "${name}: $failures file(s) failed verification. Refusing to build with them." }

    $staging = "$dest.new"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    foreach ($f in $lock.Files) {
        $target = Join-Path $staging $f.Path
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $top.FullName $f.Path) -Destination $target
    }
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    Move-Item -LiteralPath $staging -Destination $dest
    Remove-Item -LiteralPath $work -Recurse -Force
    Write-Host "${name}: downloaded and verified ($($lock.Files.Count) files)."
}

Install-Dependency 'concentus'
