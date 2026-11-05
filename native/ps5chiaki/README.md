# ps5chiaki — in-process Remote Play engine

`ps5chiaki.dll` is a flat-C shim over **libchiaki**, the protocol/streaming core
of [chiaki-ng](https://github.com/streetpea/chiaki-ng) (v1.10.1, AGPL-3.0). It
lets the .NET client run a Remote Play session entirely inside its own process:
session lifecycle, ffmpeg video decode, opus audio decode, controller state —
no external application, no Qt, no second window.

`ps5chiaki.c` is the shim source; it gets compiled as part of `chiaki-lib`.

## Rebuilding

Requirements: VS 2022, vcpkg at `C:\vcpkg`, standalone `protoc`, Python 3 with
`protobuf` (`pip install protobuf`), LLVM `clang-cl`.

```powershell
# 1. fetch chiaki-ng v1.10.1 + bundled deps
git clone https://github.com/streetpea/chiaki-ng third_party/chiaki/chiaki-ng
cd third_party/chiaki/chiaki-ng && git checkout v1.10.1
git submodule update --init third-party/curl third-party/gf-complete third-party/jerasure third-party/nanopb

# 2. apply the PS5 Suite patch set
git apply ../../../../native/ps5chiaki/patches/chiaki-ng-v1.10.1-win32.patch
cd third-party/gf-complete
git apply ../../../../../native/ps5chiaki/patches/gf-complete-intrin.patch
cd ../../..

# 3. drop the shim source into the lib
cp ../../../native/ps5chiaki/ps5chiaki.c lib/src/

# 4. vcpkg deps (classic mode — manifest mode pulls GUI deps)
C:\vcpkg\vcpkg.exe install ffmpeg[avcodec,avformat,core,swresample,swscale]:x64-windows `
    json-c:x64-windows libevent:x64-windows miniupnpc:x64-windows openssl:x64-windows opus:x64-windows

# 5. build -> native/ps5chiaki/bin/
powershell -File native/ps5chiaki/build-ps5chiaki.ps1
```

Output: `ps5chiaki.dll` + its runtime deps (avcodec, avutil, swscale,
swresample, opus, json-c, miniupnpc, event_core, libcrypto). The csproj copies
them next to the app on build; single-file publish embeds them in the exe.

## What the patches do (against chiaki-ng v1.10.1)

- `lib/CMakeLists.txt`: `CHIAKI_PS5SUITE_SHIM` option — adds `src/ps5chiaki.c`,
  exports all symbols on Windows (`WINDOWS_EXPORT_ALL_SYMBOLS`), names the
  output `ps5chiaki`, links `FFMPEG::swscale`.
- `lib/src/session.c`, `lib/src/ctrl.c`: replace 3 C99 variable-length arrays
  with `malloc`/`free` (MSVC-family compilers don't support VLAs).
- `third-party/CMakeLists.txt`: force static-only nanopb — its shared+static
  targets emit the same `protobuf-nanopb.lib` on Windows and collide.
- `third-party/gf-complete/src/gf_cpu.c`: `#include <intrin.h>` so `__cpuidex`
  is declared under clang-cl.

The build uses **clang-cl** (LLVM in VS or standalone) because MSVC `cl` also
rejects VLAs — the malloc patches cover the hot paths but clang accepts the
remaining ones in `rudp.c`/`holepunch.c` untouched.
