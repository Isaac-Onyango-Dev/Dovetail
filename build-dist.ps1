<#
    Stages Dovetail into one folder, the way it will be installed.

    All three executables must sit side by side: Dovetail.exe launches dovetail-diag.exe for
    calibration and dovetail-engine.exe for dependency repair, and it looks for them next to
    itself first. In the build tree they live in three separate bin directories and are only
    found by a recursive search up the tree, which works on this machine and would not work
    once installed. Staging here is what Stage 6 packages.
#>
param(
    [string]$Configuration = 'Release',
    [string]$Output = "$PSScriptRoot\dist\Dovetail"
)

$ErrorActionPreference = 'Stop'
# Resolved rather than hardcoded: a build agent may have the SDK somewhere else, and the
# well-known path is only a fallback for a shell that has not got dotnet on PATH.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "the .NET SDK was not found; install .NET 8 or put dotnet on PATH" }

# Refuse to stage over a running Dovetail: the file locks would produce a half-updated folder.
$running = Get-Process -Name 'Dovetail', 'dovetail-engine', 'dovetail-diag' -ErrorAction SilentlyContinue
if ($running) {
    throw ("These are running and hold the files: " +
           (($running | ForEach-Object { "$($_.Name) (pid $($_.Id))" }) -join ', ') +
           ". Close them, then run this again.")
}

# Everything except profiles is replaced. The profiles folder holds measured calibration
# data, which takes a full sweep per pad to recreate, so it is never wiped by a build.
if (Test-Path $Output) {
    Get-ChildItem $Output -Force |
        Where-Object { $_.Name -ne 'profiles' } |
        Remove-Item -Recurse -Force
}
New-Item -ItemType Directory -Path $Output -Force | Out-Null

foreach ($proj in 'Dovetail.App', 'Dovetail.Engine', 'Dovetail.Diagnostics') {
    Write-Host "publishing $proj" -ForegroundColor Cyan
    # Captured rather than streamed so a failure can print everything. Filtering the live
    # output was hiding the compiler errors behind the throw, which made a broken build look
    # like a mysterious script failure.
    $log = & $dotnet publish "$PSScriptRoot\src\$proj\$proj.csproj" `
        -c $Configuration -r win-x64 --self-contained false `
        -p:PublishSingleFile=false -p:DebugType=none `
        -o $Output --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        $log | ForEach-Object { Write-Host $_ }
        throw "$proj failed to publish"
    }
    $log | Where-Object { $_ -match 'warn' } | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
}

# The profiles folder staged here is the SEED, not the live location.
#
# Since the packaging step, the live folder is %LOCALAPPDATA%\Dovetail\profiles: Program Files
# is not writable by a standard user, a per-machine folder is shared between Windows accounts,
# and an upgrade that replaces the install folder would take the measured data with it. What
# is staged here is copied into the per-user folder once, on first run, and then left alone.
# See ProfileStore for the whole rule. A dev stage copies the real calibrations in so the
# staged build can be tested against hardware without recalibrating first.
$profOut = Join-Path $Output 'profiles'
New-Item -ItemType Directory -Path $profOut -Force | Out-Null
Get-ChildItem "$PSScriptRoot\profiles\*.calibration.json" -ErrorAction SilentlyContinue |
    ForEach-Object {
        $dest = Join-Path $profOut $_.Name
        # Do not overwrite a staged calibration that is newer than the master copy: the
        # staged folder is what gets tested against hardware, so it can legitimately be
        # ahead after a calibration run.
        if ((-not (Test-Path $dest)) -or ($_.LastWriteTimeUtc -gt (Get-Item $dest).LastWriteTimeUtc)) {
            Copy-Item $_.FullName -Destination $dest -Force
        }
    }

$exes = 'Dovetail.exe', 'dovetail-engine.exe', 'dovetail-diag.exe'
$missing = $exes | Where-Object { -not (Test-Path (Join-Path $Output $_)) }
if ($missing) { throw "staging incomplete, missing: $($missing -join ', ')" }

Write-Host ""
Write-Host "staged to $Output" -ForegroundColor Green
Get-ChildItem $Output -File | Measure-Object -Property Length -Sum |
    ForEach-Object { Write-Host ("  {0} files, {1:N1} MB" -f $_.Count, ($_.Sum / 1MB)) }
$exes | ForEach-Object {
    $v = (Get-Item (Join-Path $Output $_)).VersionInfo
    Write-Host ("  {0,-20} {1}" -f $_, $v.FileVersion)
}

Write-Host ""
Write-Host "  profiles staged here are the first-run seed; the staged build reads and writes"
Write-Host "  $env:LOCALAPPDATA\Dovetail\profiles once it runs. 'dovetail-engine deps paths' confirms it."
