#!/usr/bin/env bash
# Adds Shaderless to your app menu (and the desktop, if you have one): ~/.local/share/applications/shaderless.desktop
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPS="$HOME/.local/share/applications"
ICONS="$HOME/.local/share/icons/hicolor"
mkdir -p "$APPS"
for s in 64 128 256 512; do
    mkdir -p "$ICONS/${s}x${s}/apps"
    cp "$DIR/assets/shaderless-$s.png" "$ICONS/${s}x${s}/apps/shaderless.png"
done
mkdir -p "$ICONS/scalable/apps" && cp "$DIR/assets/shaderless.svg" "$ICONS/scalable/apps/shaderless.svg"
cat > "$APPS/shaderless.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Shaderless
GenericName=Shader stutter killer
Comment=Pre-compile your Proton games' shaders (unofficial Linux port of SCSKiller)
Exec="$DIR/run-gui.sh"
Icon=shaderless
Terminal=false
Categories=Game;Utility;
StartupWMClass=Shaderless
DESKTOP
chmod +x "$APPS/shaderless.desktop"
DESK="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
if [ -d "$DESK" ]; then cp "$APPS/shaderless.desktop" "$DESK/shaderless.desktop"; chmod +x "$DESK/shaderless.desktop"; fi
command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$ICONS" >/dev/null 2>&1 || true
echo "Shaderless added to your app menu${DESK:+ and $DESK}."
