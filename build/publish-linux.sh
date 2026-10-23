#!/usr/bin/env bash
# Publica la CLI de Roost para Linux x64: lo que lleva el tar.gz de la release.
#
# Un solo ejecutable autocontenido, `roost` (ROOST.md §9, fase 0: un ejecutable self-contained por
# SO), que no necesita .NET instalado. En Linux de momento solo sirven `scan` y `audit`; el helper y
# el shim son de Windows y no van. Al contrario que en Windows (cuatro exe que comparten un runtime),
# aquí hay un solo ejecutable, así que single-file no duplica nada.
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
  -p:PublishSingleFile=true -p:DebugType=embedded -o "$work"

mkdir -p "$out"
cp "$work/roost" "$out/roost"
chmod 755 "$out/roost"
cp "$root/LICENSE" "$out/LICENSE.txt"

# Lo que deje la publicación además de roost tiene que ser lo que no va (pdb, la documentación XML
# de los paquetes NuGet, el helper y el shim): si un día aparece una librería nativa suelta, roost
# la necesitaría a su lado.
leftovers="$(cd "$work" && ls -A | grep -v -E '^(roost|.*\.pdb|Roost\..*\.xml|roost-helper.*|roost-shim.*)$' || true)"
if [ -n "$leftovers" ]; then
  echo "La publicacion deja ficheros que roost podria necesitar:" >&2
  echo "$leftovers" >&2
  exit 1
fi

echo "==> $out: $(ls -A "$out" | tr '\n' ' ')($(du -sh "$out/roost" | cut -f1))"
