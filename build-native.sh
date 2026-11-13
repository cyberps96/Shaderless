#!/usr/bin/env bash
# Builds Shaderless's three Windows-side binaries with mingw-w64 (they run inside Proton):
#   src/Shaderless.Core/Native/d3d12.dll           the recorder (recorder.cpp, exports as in d3d12.def)
#   src/Shaderless.Core/Native/dxgi.dll            the Streamline companion proxy (dxgi.cpp, exports as in dxgi.def)
#   src/Shaderless.Core/Native/shaderless_warm.exe the warmer   (warmer.cpp + upstream proxy/warm11.cpp)
# Needs: x86_64-w64-mingw32-g++ (Arch: mingw-w64-gcc, Debian/Ubuntu: g++-mingw-w64-x86-64-posix)
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
N="$DIR/src/Shaderless.Core/Native"
CXX="${CXX:-x86_64-w64-mingw32-g++}"
FLAGS=(-O2 -s -std=c++20 -static -static-libgcc -static-libstdc++)

echo "== d3d12.dll (recorder)"
"$CXX" "${FLAGS[@]}" -shared -o "$N/d3d12.dll" "$N/recorder.cpp" "$N/d3d12.def" -lbcrypt -ldxguid -luuid

echo "== dxgi.dll (Streamline companion proxy)"
"$CXX" "${FLAGS[@]}" -shared -o "$N/dxgi.dll" "$N/dxgi.cpp" "$N/dxgi.def"

echo "== shaderless_warm.exe (warmer)"
"$CXX" "${FLAGS[@]}" -municode -I"$DIR/upstream/proxy" -o "$N/shaderless_warm.exe" "$N/warmer.cpp" "$DIR/upstream/proxy/warm11.cpp" \
    -ld3d12 -ld3d11 -ldxgi -ld3dcompiler -lbcrypt -ldxguid -luuid -lole32

echo "done"
