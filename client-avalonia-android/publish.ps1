# Publish the Android APK.
#
# The Xamarin.Android toolchain (manifest merger, apksigner, …) needs JDK 17+.
# JAVA_HOME on this machine points at JDK 11, so we auto-detect a suitable JDK:
#   1. Android Studio's bundled JBR (always present when AS is installed)
#   2. JAVA_HOME, if it already points to 17+
#   3. Common JDK install roots (Adoptium, Microsoft, Corretto, Zulu, Oracle)
#   4. %LOCALAPPDATA%\Programs (per-user JDKs)
# The winner is passed as -p:JavaSdkDirectory so we never touch the
# machine-wide JAVA_HOME.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$Project = Join-Path $PSScriptRoot "PS5SuiteAndroid.csproj"

function Get-JavaMajorVersion([string]$jdkDir) {
    $javaExe = Join-Path $jdkDir "bin\java.exe"
    if (!(Test-Path $javaExe)) { return 0 }
    # java -version prints to stderr; "17.0.12" -> 17, "1.8.0_503" -> 8.
    # Continue preference so stderr text doesn't raise NativeCommandError.
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $verLine = (& $javaExe -version 2>&1 | Select-Object -First 1) -join " "
    $ErrorActionPreference = $prev
    if ($verLine -match '"(\d+)\.(\d+)') {
        if ([int]$Matches[1] -eq 1) { return [int]$Matches[2] }
        return [int]$Matches[1]
    }
    return 0
}

function Find-Jdk {
    $candidates = [System.Collections.Generic.List[string]]::new()

    # Android Studio bundled JBR first — guaranteed compatible with the
    # Android SDK tooling this project was tested against.
    $candidates.Add("C:\Program Files\Android\Android Studio\jbr")
    $asLocal = Join-Path $env:LOCALAPPDATA "Programs\Android Studio\jbr"
    $candidates.Add($asLocal)

    # Existing JAVA_HOME is acceptable if it is new enough.
    if ($env:JAVA_HOME) { $candidates.Add($env:JAVA_HOME) }

    # Sweep the usual vendor dirs for jdk-17+/jdk-21+ installs.
    foreach ($root in @(
        "C:\Program Files\Eclipse Adoptium",
        "C:\Program Files\Microsoft",
        "C:\Program Files\Amazon Corretto",
        "C:\Program Files\Zulu",
        "C:\Program Files\Java",
        "$env:LOCALAPPDATA\Programs\Eclipse Adoptium",
        "$env:LOCALAPPDATA\Programs\Microsoft"
    )) {
        if (Test-Path $root) {
            Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match 'jdk|jbr|jdk-?\d+' } |
                ForEach-Object { $candidates.Add($_.FullName) }
        }
    }

    foreach ($dir in $candidates) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        $major = Get-JavaMajorVersion $dir
        if ($major -ge 17) {
            Write-Host "Using JDK $major at $dir" -ForegroundColor Cyan
            return $dir
        }
    }
    return $null
}

$jdk = Find-Jdk
if (!$jdk) {
    Write-Error @"
No JDK 17+ found. Install one of:
  - Android Studio (bundles JBR at C:\Program Files\Android\Android Studio\jbr)
  - https://adoptium.net/ (Temurin 17 or 21)
"@
}

Write-Host "Publishing PS5SuiteAndroid ($Configuration) ..." -ForegroundColor Cyan
dotnet publish $Project -c $Configuration -p:JavaSdkDirectory="$jdk"
if ($LASTEXITCODE -ne 0) { Write-Error "Android publish failed" }

$publishDir = Join-Path $PSScriptRoot "bin\$Configuration\net10.0-android\publish"
$apk = Get-Item (Join-Path $publishDir "PS5Suite.apk") -ErrorAction SilentlyContinue
if (!$apk) {
    $apk = Get-ChildItem $publishDir -Filter "*-Signed.apk" -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if ($apk) {
    Write-Host ""
    Write-Host "APK: $($apk.FullName)" -ForegroundColor Green
    Write-Host ("Size: {0:N1} MB" -f ($apk.Length / 1MB)) -ForegroundColor Green
}
