# build-ps5chiaki.ps1 - builds the in-process Remote Play engine (ps5chiaki.dll).
#
# Output: native/ps5chiaki/bin/
#   ps5chiaki.dll   (libchiaki + flat C shim, shared)
#   *.dll deps      (avcodec/avutil/swscale/swresample, opus, json-c,
#                    libevent, miniupnpc, libcrypto - from vcpkg)
#
# Requires: VS 2022 (cl), vcpkg at C:\vcpkg with x64-windows deps installed.

param(
    [string]$VcpkgRoot = 'C:\vcpkg',
    [string]$Triplet = 'x64-windows'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$src  = Join-Path $repo 'third_party\chiaki\chiaki-ng'
$out  = Join-Path $PSScriptRoot 'bin'
$bld  = Join-Path $PSScriptRoot 'build'

$cmake = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
$vcvars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'

if (-not (Test-Path $src)) { throw "chiaki-ng source missing at $src" }
if (-not (Test-Path $cmake)) { throw "VS cmake not found" }
if (-not (Test-Path "$VcpkgRoot\installed\$Triplet")) { throw "vcpkg $Triplet deps missing at $VcpkgRoot" }

# write a temporary .cmd so cmd.exe does the vcvars+cmake line (avoids PS quoting hell)
$gitdir = 'C:\vcpkg\downloads\tools\git-2.56.0.windows.4-windows\cmd'
$protocdir = 'C:\tools\protoc\prot\bin'
$clangdir = 'C:\Program Files\LLVM\bin'
if (Test-Path $bld) { Remove-Item -Recurse -Force $bld }
$cmdFile = Join-Path $env:TEMP 'ps5chiaki_build.cmd'
@"
@echo off
set "PATH=$clangdir;$protocdir;$gitdir;%PATH%"
call "$vcvars" x64 >nul 2>&1
"$cmake" -S "$src" -B "$bld" -G Ninja -DCMAKE_MAKE_PROGRAM="$ninja" -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang-cl -DCMAKE_CXX_COMPILER=clang-cl -DBUILD_SHARED_LIBS=ON -DCMAKE_TOOLCHAIN_FILE="$VcpkgRoot\scripts\buildsystems\vcpkg.cmake" -DVCPKG_TARGET_TRIPLET=$Triplet -DVCPKG_MANIFEST_MODE=OFF -DCHIAKI_ENABLE_GUI=OFF -DCHIAKI_ENABLE_CLI=OFF -DCHIAKI_ENABLE_TESTS=OFF -DCHIAKI_ENABLE_SETSU=OFF -DCHIAKI_ENABLE_BOREALIS=OFF -DCHIAKI_ENABLE_STEAMDECK_NATIVE=OFF -DCHIAKI_ENABLE_STEAM_SHORTCUT=OFF -DCHIAKI_ENABLE_FFMPEG_DECODER=ON -DCHIAKI_ENABLE_PI_DECODER=OFF -DCHIAKI_LIB_ENABLE_OPUS=ON -DCHIAKI_ENABLE_SPEEX=OFF -DCHIAKI_PS5SUITE_SHIM=ON >"$bld-configure.log" 2>&1
if errorlevel 1 exit /b 1
"$cmake" --build "$bld" --target chiaki-lib >"$bld-build.log" 2>&1
if errorlevel 1 exit /b 2
exit /b 0
"@ | Set-Content -Encoding ASCII $cmdFile

Write-Host '== configure + build ==' -ForegroundColor Cyan
& cmd.exe /c $cmdFile
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED (code $LASTEXITCODE) - last lines of logs:" -ForegroundColor Red
    Get-Content "$bld-configure.log" -Tail 20 -ErrorAction SilentlyContinue
    Get-Content "$bld-build.log" -Tail 20 -ErrorAction SilentlyContinue
    exit $LASTEXITCODE
}
Get-Content "$bld-build.log" -Tail 8

Write-Host '== stage ==' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $out | Out-Null
$built = @("$bld\lib\ps5chiaki.dll", "$bld\lib\chiaki.dll") | Where-Object { Test-Path $_ } | Select-Object -First 1
Copy-Item $built (Join-Path $out 'ps5chiaki.dll') -Force
foreach ($d in 'libcrypto-3-x64.dll','opus.dll','json-c.dll','miniupnpc.dll','event_core.dll','avcodec-63.dll','avutil-61.dll','swscale-10.dll','swresample-7.dll') {
    Copy-Item "$VcpkgRoot\installed\$Triplet\bin\$d" $out -Force
}
Get-ChildItem $out | ForEach-Object { Write-Host ('  {0}  {1:N0} B' -f $_.Name, $_.Length) }
Write-Host "Done -> $out" -ForegroundColor Green
