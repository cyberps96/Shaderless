#!/usr/bin/env bash
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export __GL_SHADER_DISK_CACHE=1
export __GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1

if [ -x "$DIR/bin/shaderless-cli" ]; then
    exec "$DIR/bin/shaderless-cli" "$@"
fi

if [ -x "$DIR/shaderless-cli" ]; then
    exec "$DIR/shaderless-cli" "$@"
fi

for CFG in Release Debug; do
    BIN="$DIR/src/Shaderless.Cli/bin/$CFG/net10.0/shaderless-cli"
    if [ -x "$BIN" ] && [ -z "$(find "$DIR/src" "$DIR/upstream" \( -name bin -o -name obj \) -prune -o -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.props' -o -name '*.dll' -o -name '*.exe' \) -newer "$BIN" -print -quit 2>/dev/null)" ]; then
        exec "$BIN" "$@"
    fi
done

exec dotnet run --project "$DIR/src/Shaderless.Cli/Shaderless.Cli.csproj" --no-launch-profile -- "$@"
