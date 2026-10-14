#!/usr/bin/env bash
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export __GL_SHADER_DISK_CACHE=1
export __GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1
export RADV_PERFTEST=aco

exec dotnet run --project "$DIR/src/SCSKiller.Linux.Cli/SCSKiller.Linux.Cli.csproj" --no-launch-profile -- "$@"
