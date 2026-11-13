#!/usr/bin/env bash
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Fix Wayland protocol & WebKitGTK DMA-BUF rendering compatibility on NVIDIA
export GDK_BACKEND=x11
export WEBKIT_DISABLE_DMABUF_RENDERER=1

# GPU driver shader caching optimizations
export __GL_SHADER_DISK_CACHE=1
export __GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1

if [ -x "$DIR/bin/Shaderless" ]; then
    exec "$DIR/bin/Shaderless" "$@"
fi

if [ -x "$DIR/Shaderless" ]; then
    exec "$DIR/Shaderless" "$@"
fi

for CFG in Release Debug; do
    BIN="$DIR/src/Shaderless.App/bin/$CFG/net10.0/Shaderless"
    if [ -x "$BIN" ] && [ -z "$(find "$DIR/src" "$DIR/upstream" \( -name bin -o -name obj \) -prune -o -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.props' -o -name '*.dll' -o -name '*.exe' \) -newer "$BIN" -print -quit 2>/dev/null)" ]; then
        exec "$BIN" "$@"
    fi
done

exec dotnet run --project "$DIR/src/Shaderless.App/Shaderless.App.csproj" --no-launch-profile -- "$@"
