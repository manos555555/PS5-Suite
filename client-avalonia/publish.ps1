<#
.SYNOPSIS
    Builds standalone single-file executables of PS5Suite for all major platforms.

.DESCRIPTION
    Produces self-contained, single-file binaries (no .NET runtime required on target machine) for:
      - Windows x64       -> dist/win-x64/PS5Suite.exe
      - Linux x64         -> dist/linux-x64/PS5Suite
      - Linux ARM64       -> dist/linux-arm64/PS5Suite
      - macOS x64 (Intel) -> dist/osx-x64/PS5Suite
      - macOS ARM64 (M1+) -> dist/osx-arm64/PS5Suite

.PARAMETER Targets
    Optional list of RIDs to build. Defaults to all 5.
    Example: .\publish.ps1 -Targets win-x64,linux-x64

.PARAMETER Clean
    Wipe the dist/ folder before building.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Targets win-x64
    .\publish.ps1 -Clean
#>

param(
    [string[]] $Targets = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [switch]   $Clean
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project     = Join-Path $ProjectRoot 'PS5Suite.csproj'
$DistRoot    = Join-Path $ProjectRoot 'dist'

if (-not (Test-Path $Project)) {
    Write-Error "Project file not found: $Project"
    exit 1
}

if ($Clean -and (Test-Path $DistRoot)) {
    Write-Host "Cleaning $DistRoot ..." -ForegroundColor Yellow
    Remove-Item $DistRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $DistRoot -Force | Out-Null

$summary = @()

foreach ($rid in $Targets) {
    $outDir = Join-Path $DistRoot $rid

    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host " Publishing $rid" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan

    $args = @(
        'publish', $Project,
        '-c', 'Release',
        '-r', $rid,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $outDir
    )

    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed for $rid"
        $summary += [pscustomobject]@{ RID = $rid; Status = 'FAILED'; Path = '-'; SizeMB = '-' }
        continue
    }

    # Locate the produced binary
    $exeName = if ($rid -like 'win-*') { 'PS5Suite.exe' } else { 'PS5Suite' }
    $exePath = Join-Path $outDir $exeName

    if (Test-Path $exePath) {
        $sizeMB = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
        Write-Host ("  -> {0} ({1} MB)" -f $exePath, $sizeMB) -ForegroundColor Green
        $summary += [pscustomobject]@{ RID = $rid; Status = 'OK'; Path = $exePath; SizeMB = $sizeMB }
    } else {
        Write-Warning "Binary not found at expected path: $exePath"
        $summary += [pscustomobject]@{ RID = $rid; Status = 'NO BINARY'; Path = $outDir; SizeMB = '-' }
    }
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Build summary" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
$summary | Format-Table -AutoSize

Write-Host ""
Write-Host "Notes:" -ForegroundColor Yellow
Write-Host " - Linux/macOS users may need: chmod +x ./PS5Suite"
Write-Host " - macOS may also need: xattr -dr com.apple.quarantine ./PS5Suite"
Write-Host " - All builds are self-contained; no .NET runtime needed on the target system."
