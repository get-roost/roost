#!/usr/bin/env bash
# Publica Roost para Linux x64: lo que lleva el tar.gz de la release.
#
# `roost`, la CLI (con el daemon dentro), y `roost-helper`, lo único que se ejecuta con privilegios
# (por pkexec o sudo: el hosts, /var/www/roost, el setcap de nginx, la CA del sistema, el DNS local),
# cada uno un ejecutable autocontenido que no necesita .NET instalado, y la política de polkit del
# helper (dev.roost.helper.policy, que lo espera en /usr/libexec/roost/roost-helper). El shim y la
# app llegan con el paquete de Linux.
#
# Lo usa el workflow Release (.github/workflows/release.yml); también funciona en local, incluso
# desde Windows con Git Bash (publicación cruzada).
#
#   bash build/publish-linux.sh [carpeta-de-salida]     # por defecto, artifacts/linux-x64
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$root/artifacts/linux-x64}"
work="$root/artifacts/.publish/linux-x64"

rm -rf "$work" "$out"
echo "==> dotnet publish src/Roost.Cli (linux-x64, single-file)"
dotnet publish "$root/src/Roost.Cli/Roost.Cli.csproj" -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:DebugType=embedded -o "$work/cli"
echo "==> dotnet publish src/Roost.Helper (linux-x64, single-file, recortado)"
# Recortado (PublishTrimmed): el helper es poco código y sin reflexión, y así pesa 17 MB en vez de 73.
dotnet publish "$root/src/Roost.Helper/Roost.Helper.csproj" -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:DebugType=embedded -p:PublishTrimmed=true -o "$work/helper"

mkdir -p "$out"
cp "$work/cli/roost" "$out/roost"
cp "$work/helper/roost-helper" "$out/roost-helper"
chmod 755 "$out/roost" "$out/roost-helper"
cp "$root/build/linux/dev.roost.helper.policy" "$out/dev.roost.helper.policy"
cp "$root/LICENSE" "$out/LICENSE.txt"

# Lo que deje cada publicación además de su ejecutable tiene que ser lo que no va (pdb, la
# documentación XML de los paquetes NuGet, el helper y el shim que arrastra la CLI): si un día aparece
# una librería nativa suelta, el ejecutable la necesitaría a su lado.
leftovers="$(cd "$work/cli" && ls -A | grep -v -E '^(roost|.*\.pdb|Roost\..*\.xml|roost-helper.*|roost-shim.*)$' || true)"
leftovers="$leftovers$(cd "$work/helper" && ls -A | grep -v -E '^(roost-helper|.*\.pdb)$' || true)"
if [ -n "$leftovers" ]; then
  echo "La publicacion deja ficheros que roost o roost-helper podrian necesitar:" >&2
  echo "$leftovers" >&2
  exit 1
fi

echo "==> $out: $(ls -A "$out" | tr '\n' ' ')($(du -sh "$out/roost" | cut -f1) + $(du -sh "$out/roost-helper" | cut -f1))"
