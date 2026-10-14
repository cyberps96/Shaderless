#!/usr/bin/env bash
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Fix Wayland protocol & WebKitGTK DMA-BUF rendering compatibility on NVIDIA
export GDK_BACKEND=x11
export WEBKIT_DISABLE_DMABUF_RENDERER=1

# GPU driver shader caching optimizations
export __GL_SHADER_DISK_CACHE=1
export __GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1
export RADV_PERFTEST=aco

exec dotnet run --project "$DIR/src/SCSKiller.Linux.App/SCSKiller.Linux.App.csproj" --no-launch-profile
