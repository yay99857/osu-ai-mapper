#!/usr/bin/env bash
# Publishes the desktop app and the CLI for this user and adds the app to the application launcher
# (Walker on Omarchy, or any freedesktop launcher). Like the Windows installer, the installed app
# still uses the Python tools and Mapperatorinator in this checkout, named in osu-ai-mapper.json.
#
#   tools/linux/install-desktop.sh            # install or update
#   tools/linux/install-desktop.sh --remove   # uninstall (projects and weights are not touched)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
prefix="$data/osu-ai-mapper"
bin="$HOME/.local/bin"
desktop="$data/applications/osu-ai-mapper.desktop"
icon="$data/icons/hicolor/256x256/apps/osu-ai-mapper.png"

if [[ "${1:-}" == "--remove" ]]; then
    rm -rf "$prefix" "$desktop" "$icon" "$bin/osu-ai"
    update-desktop-database "$data/applications" 2>/dev/null || true
    echo "Removido. Projetos, o checkout e o cache do Hugging Face continuam onde estavam."
    exit 0
fi

uv="$(command -v uv || true)"
config() {
    printf '{ "repositoryRoot": "%s", "uv": "%s" }\n' "$repo" "${uv:-uv}" >"$1/osu-ai-mapper.json"
}

echo "==> Publicando o app e a CLI em $prefix"
rm -rf "$prefix/app" "$prefix/cli"
dotnet publish "$repo/apps/desktop" -c Release -f net10.0 -o "$prefix/app" --nologo
dotnet publish "$repo/apps/cli" -c Release -o "$prefix/cli" --nologo
config "$prefix/app"
config "$prefix/cli"

mkdir -p "$bin" "$(dirname "$desktop")" "$(dirname "$icon")"
cat >"$bin/osu-ai" <<EOF
#!/usr/bin/env bash
exec "$prefix/cli/osu-ai" "\$@"
EOF
chmod +x "$bin/osu-ai"

# The .ico carries a 256×256 frame (index 6).
if command -v magick >/dev/null; then
    magick "$repo/apps/desktop/Assets/app.ico[6]" "$icon"
else
    ffmpeg -loglevel error -y -i "$repo/apps/desktop/Assets/app.ico" -map 0:6 "$icon"
fi

cat >"$desktop" <<EOF
[Desktop Entry]
Type=Application
Name=osu! AI Mapper
Comment=Rascunhos editáveis de osu!standard com Mapperatorinator
Exec="$prefix/app/osu-ai-mapper"
Icon=osu-ai-mapper
Terminal=false
Categories=AudioVideo;Audio;Game;
StartupWMClass=osu-ai-mapper
EOF
update-desktop-database "$data/applications" 2>/dev/null || true
gtk-update-icon-cache -q "$data/icons/hicolor" 2>/dev/null || true

echo "Instalado: 'osu! AI Mapper' no launcher (Super+Espaço) e 'osu-ai' no terminal."
