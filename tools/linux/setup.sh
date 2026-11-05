#!/usr/bin/env bash
# Prepares a Linux checkout (tested on Omarchy/Arch): the Python analysis environment and a local
# Mapperatorinator checkout with its own virtual environment in artifacts/external/Mapperatorinator.
#
#   tools/linux/setup.sh                 # PyTorch for CPU (default)
#   TORCH_INDEX=https://download.pytorch.org/whl/<rocmX.Y|cuXYZ> tools/linux/setup.sh   # supported GPU
#   MAPPERATORINATOR_REF=<commit|main> tools/linux/setup.sh                       # another revision
#
# A new checkout goes to the revision in tools/linux/mapperatorinator.ref, the one this integration
# was last verified with; update that file after verifying a newer one. Re-running is safe: an
# existing checkout is not moved to another revision, only its packages are brought up to date. The v32 weights (~1 GB) are downloaded by Mapperatorinator on the first map.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
target="$repo/artifacts/external/Mapperatorinator"
ref="${MAPPERATORINATOR_REF:-$(tr -d '[:space:]' <"$repo/tools/linux/mapperatorinator.ref")}"
torch_index="${TORCH_INDEX:-https://download.pytorch.org/whl/cpu}"
torch_version="${TORCH_VERSION:-2.10.*}"

missing=()
for tool in dotnet uv git ffmpeg mpv; do
    command -v "$tool" >/dev/null || missing+=("$tool")
done
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    missing+=("dotnet-sdk")
fi
if ((${#missing[@]})); then
    echo "Faltam: ${missing[*]}" >&2
    echo "No Omarchy/Arch: sudo pacman -S --needed dotnet-sdk git ffmpeg mpv  (uv: https://docs.astral.sh/uv/)" >&2
    exit 1
fi

echo "==> Análise de áudio (engines/audio-analysis)"
uv sync --locked --project "$repo/engines/audio-analysis"

echo "==> Mapperatorinator em $target"
if [[ ! -d "$target/.git" ]]; then
    mkdir -p "$(dirname "$target")"
    git clone https://github.com/OliBomby/Mapperatorinator.git "$target"
    if [[ -n "$ref" ]]; then
        git -C "$target" checkout --detach "$ref"
    fi
elif [[ -n "${MAPPERATORINATOR_REF:-}" ]]; then
    echo "    checkout existente mantido; para mudar de revisão use git -C '$target' checkout $ref" >&2
fi
current="$(git -C "$target" rev-parse HEAD)"
echo "    revisão: ${current:0:7}"
if [[ "$ref" =~ ^[0-9a-f]{40}$ && "$current" != "$ref" ]]; then
    echo "    aviso: a integração foi verificada com ${ref:0:7}; este checkout está em ${current:0:7}" >&2
fi

cd "$target"
if [[ ! -x .venv/bin/python ]]; then
    # Mapperatorinator recommends Python 3.10; later versions may break its dependencies.
    uv venv --python 3.10 .venv
fi

echo "==> PyTorch $torch_version de $torch_index"
uv pip install --python .venv/bin/python "torch==$torch_version" "torchaudio==$torch_version" --index-url "$torch_index"

echo "==> Dependências do Mapperatorinator (sem a interface web: pywebview/Qt)"
filtered="$(mktemp)"
trap 'rm -f "$filtered"' EXIT
grep -vE '^(pywebview|pyqtwebengine)' requirements.txt >"$filtered"
uv pip install --python .venv/bin/python -r "$filtered" \
    --extra-index-url "$torch_index" --index-strategy unsafe-best-match

echo "==> Verificação"
.venv/bin/python - <<'PY'
import torch
print(f"    torch {torch.__version__}, aceleração: {'GPU ' + torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'CPU'}")
try:
    import torchcodec  # noqa: F401
    print("    torchcodec ok")
except Exception as e:  # FFmpeg libraries the wheel was not built for
    print(f"    aviso: torchcodec não carregou ({e.__class__.__name__}: {e})")
PY

echo
echo "Pronto. Rode o app com:  dotnet run --project apps/desktop -c Release -f net10.0"
echo "Ou instale no launcher:  tools/linux/install-desktop.sh"
