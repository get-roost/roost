#!/usr/bin/env python3
"""Los paquetes de Roost para Scoop y winget, a partir de una release de GitHub.

Lo usa .github/workflows/packages.yml con cada release estable, y sirve igual a mano:

    python3 packaging/packages.py scoop  --version 0.1.0 --sums SHA256SUMS.txt
    python3 packaging/packages.py winget --version 0.1.0 --sums SHA256SUMS.txt --out salida/
    python3 packaging/packages.py check  [--scoop bucket/roost.json] [--winget salida/manifests/r/Roost/Roost/0.1.0]

- `scoop` pone en bucket/roost.json la versión, la url del zip y su SHA-256.
- `winget` rellena las plantillas de packaging/winget/ y deja los manifiestos con la estructura de
  microsoft/winget-pkgs (manifests/r/Roost/Roost/<versión>/), listos para `wingetcreate submit`.
- `check` valida el manifiesto de Scoop y los de winget contra sus esquemas oficiales. Es lo único
  que necesita algo de fuera: `pip install jsonschema pyyaml` (los esquemas los baja de GitHub si no
  se le pasa --schemas con una carpeta que ya los tenga).

El SHA-256 sale de SHA256SUMS.txt de la release (formato de sha256sum: «<sha256>  <fichero>») o se
pasa con --sha256. Solo van a Scoop y a winget las versiones estables (x.y.z, sin «-»).
Solo biblioteca estándar (Python 3.8+), salvo `check`.
"""

import argparse
import datetime
import json
import pathlib
import re
import sys
import tempfile
import urllib.request

REPO = "get-roost/roost"
PACKAGE_ID = "Roost.Roost"
ROOT = pathlib.Path(__file__).resolve().parent.parent
BUCKET_MANIFEST = ROOT / "bucket" / "roost.json"
WINGET_TEMPLATES = ROOT / "packaging" / "winget"

# El comentario del manifiesto de Scoop una vez que apunta a una release de verdad.
SCOOP_COMMENT = (
    "Lo mantiene .github/workflows/packages.yml (packaging/packages.py scoop): con cada release "
    "estable cambia version, url y hash, sacado de SHA256SUMS.txt."
)

# Los esquemas oficiales con los que valida `check`. Los de winget son los de la 1.12.0, la que usa
# hoy microsoft/winget-pkgs (Tools/YamlCreate.ps1 y komac); la carpeta v1.12.0 ya no cambia.
WINGET_SCHEMA_URL = (
    "https://raw.githubusercontent.com/microsoft/winget-cli/master/schemas/JSON/manifests/"
    "v1.12.0/manifest.{type}.1.12.0.json"
)
SCOOP_SCHEMA_URL = "https://raw.githubusercontent.com/ScoopInstaller/Scoop/master/schema.json"
WINGET_TYPES = ("version", "installer", "defaultLocale", "locale")

STABLE_VERSION = re.compile(r"^\d+\.\d+\.\d+$")
SHA256 = re.compile(r"^[0-9a-fA-F]{64}$")


class PackagingError(Exception):
    """Un error para enseñarlo tal cual."""


def asset_name(version):
    return f"roost-{version}-win-x64.zip"


def asset_url(version):
    return f"https://github.com/{REPO}/releases/download/v{version}/{asset_name(version)}"


def stable_version(text):
    version = text[1:] if text.startswith("v") else text
    if not STABLE_VERSION.match(version):
        raise PackagingError(
            f"'{text}' no es una version estable x.y.z: las prereleases no van a Scoop ni a winget."
        )
    return version


def version_key(version):
    return tuple(int(p) for p in version.split("."))


def sha_from_sums(sums_path, file_name):
    """El SHA-256 de un fichero en un SHA256SUMS.txt (admite el «*» del modo binario y CRLF)."""
    text = pathlib.Path(sums_path).read_text(encoding="utf-8-sig")
    for line in text.splitlines():
        parts = line.strip().split(None, 1)
        if len(parts) == 2 and parts[1].lstrip("*") == file_name:
            if not SHA256.match(parts[0]):
                raise PackagingError(f"El SHA-256 de {file_name} en {sums_path} no es valido: {parts[0]}")
            return parts[0].lower()
    raise PackagingError(f"{sums_path} no trae {file_name}.")


def resolve_sha(args, version):
    if args.sha256:
        if not SHA256.match(args.sha256):
            raise PackagingError(f"--sha256 no es un SHA-256: {args.sha256}")
        return args.sha256.lower()
    if args.sums:
        return sha_from_sums(args.sums, asset_name(version))
    raise PackagingError("Hace falta --sha256 o --sums.")


def write_text(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


# ---------------------------------------------------------------- scoop

def cmd_scoop(args):
    version = stable_version(args.version)
    sha = resolve_sha(args, version)
    path = pathlib.Path(args.manifest)
    manifest = json.loads(path.read_text(encoding="utf-8"))

    current = manifest.get("version", "0.0.0")
    if STABLE_VERSION.match(current) and version_key(version) < version_key(current) and not args.force:
        raise PackagingError(
            f"{path} ya esta en la {current}: no se baja a la {version} (usa --force si es lo que quieres)."
        )

    manifest["##"] = SCOOP_COMMENT
    manifest["version"] = version
    arch = manifest["architecture"]["64bit"]
    arch["url"] = asset_url(version)
    arch["hash"] = sha
    write_text(path, json.dumps(manifest, indent=4, ensure_ascii=False) + "\n")
    print(f"{path}: Roost {version}, {asset_name(version)} con SHA-256 {sha}")
    return 0


# ---------------------------------------------------------------- winget

def render_winget(version, sha, release_date, out_dir):
    """Rellena las plantillas y devuelve la carpeta de la versión (manifests/r/Roost/Roost/<v>)."""
    target = pathlib.Path(out_dir) / "manifests" / "r" / "Roost" / "Roost" / version
    values = {
        "{{version}}": version,
        "{{url}}": asset_url(version),
        "{{sha256}}": sha.upper(),
        "{{release_date}}": release_date,
    }
    templates = sorted(WINGET_TEMPLATES.glob(f"{PACKAGE_ID}*.yaml"))
    if not templates:
        raise PackagingError(f"No hay plantillas de winget en {WINGET_TEMPLATES}.")
    header = f"# Created with packaging/packages.py from https://github.com/{REPO}\n"
    for template in templates:
        text = template.read_text(encoding="utf-8")
        for key, value in values.items():
            text = text.replace(key, value)
        if "{{" in text:
            raise PackagingError(f"{template.name}: queda un marcador sin rellenar.")
        write_text(target / template.name, header + text)
    return target


def cmd_winget(args):
    version = stable_version(args.version)
    sha = resolve_sha(args, version)
    release_date = args.release_date or datetime.datetime.now(datetime.timezone.utc).date().isoformat()
    datetime.date.fromisoformat(release_date)  # falla si no es AAAA-MM-DD
    target = render_winget(version, sha, release_date, args.out)
    for f in sorted(target.iterdir()):
        print(f)
    return 0


# ---------------------------------------------------------------- check

def load_schemas(schema_dir):
    """Los esquemas oficiales: de --schemas si están, si no se bajan a una carpeta temporal."""
    folder = pathlib.Path(schema_dir) if schema_dir else pathlib.Path(tempfile.mkdtemp(prefix="roost-schemas-"))
    wanted = {f"manifest.{t}.1.12.0.json": WINGET_SCHEMA_URL.format(type=t) for t in WINGET_TYPES}
    wanted["scoop.schema.json"] = SCOOP_SCHEMA_URL
    schemas = {}
    for name, url in wanted.items():
        path = folder / name
        if not path.exists():
            folder.mkdir(parents=True, exist_ok=True)
            request = urllib.request.Request(url, headers={"User-Agent": "roost-packaging"})
            with urllib.request.urlopen(request, timeout=60) as response:
                path.write_bytes(response.read())
        schemas[name] = json.loads(path.read_text(encoding="utf-8-sig"))
    return schemas


def schema_errors(validator_cls, schema, document):
    validator = validator_cls(schema, format_checker=validator_cls.FORMAT_CHECKER)
    for error in sorted(validator.iter_errors(document), key=lambda e: list(e.absolute_path)):
        where = "/".join(str(p) for p in error.absolute_path) or "(raiz)"
        yield f"{where}: {error.message}"


def check_scoop(path, schemas, validator_cls):
    manifest = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    problems = list(schema_errors(validator_cls, schemas["scoop.schema.json"], manifest))
    version = manifest.get("version", "")
    arch = manifest.get("architecture", {}).get("64bit", {})
    if arch.get("url") != asset_url(version):
        problems.append(f"architecture/64bit/url no es la del zip de la {version}: {arch.get('url')}")
    template = manifest.get("autoupdate", {}).get("architecture", {}).get("64bit", {}).get("url", "")
    if template.replace("$version", version) != asset_url(version):
        problems.append(f"autoupdate no da la misma url que la version actual: {template}")
    if "SHA256SUMS.txt" not in json.dumps(manifest.get("autoupdate", {}).get("hash", {})):
        problems.append("autoupdate/hash no sale de SHA256SUMS.txt")
    # Actualizar o desinstalar con el daemon viejo en marcha dejaría sus dll en uso: los dos paran el stack.
    for hook in ("pre_install", "pre_uninstall"):
        if " down" not in json.dumps(manifest.get(hook, "")):
            problems.append(f"{hook} no hace roost down")
    return problems


def check_winget(folder, schemas, validator_cls, yaml):
    folder = pathlib.Path(folder)
    files = sorted(folder.glob("*.yaml"))
    if not files:
        return [f"{folder}: no hay manifiestos .yaml"]
    problems, docs = [], {}
    for f in files:
        doc = yaml.safe_load(f.read_text(encoding="utf-8"))
        # YAML convierte ReleaseDate en una fecha; el esquema la quiere como texto.
        doc = {k: (v.isoformat() if isinstance(v, datetime.date) else v) for k, v in doc.items()}
        kind = doc.get("ManifestType")
        if kind not in WINGET_TYPES:
            problems.append(f"{f.name}: ManifestType desconocido: {kind}")
            continue
        schema = schemas[f"manifest.{kind}.1.12.0.json"]
        problems += [f"{f.name}: {p}" for p in schema_errors(validator_cls, schema, doc)]
        docs[f.name] = doc

    for key in ("PackageIdentifier", "PackageVersion", "ManifestVersion"):
        values = {d.get(key) for d in docs.values()}
        if len(values) != 1:
            problems.append(f"{key} no es igual en todos los manifiestos: {sorted(map(str, values))}")
    package_id = next(iter(docs.values())).get("PackageIdentifier") if docs else PACKAGE_ID
    version_doc = docs.get(f"{package_id}.yaml")
    if not version_doc or version_doc.get("ManifestType") != "version":
        problems.append(f"Falta {package_id}.yaml (ManifestType: version)")
    if not any(d.get("ManifestType") == "installer" for d in docs.values()):
        problems.append("Falta el manifiesto del instalador")
    defaults = [d for d in docs.values() if d.get("ManifestType") == "defaultLocale"]
    if len(defaults) != 1:
        problems.append("Tiene que haber exactamente un manifiesto defaultLocale")
    elif version_doc and defaults[0].get("PackageLocale") != version_doc.get("DefaultLocale"):
        problems.append("DefaultLocale no casa con el PackageLocale del defaultLocale")
    for name, doc in docs.items():
        locale = doc.get("PackageLocale")
        if locale and name != f"{package_id}.locale.{locale}.yaml":
            problems.append(f"{name}: deberia llamarse {package_id}.locale.{locale}.yaml")
    if folder.name != str(next(iter(docs.values()), {}).get("PackageVersion")):
        problems.append(f"La carpeta {folder.name} no es la PackageVersion")
    return problems


def cmd_check(args):
    try:
        import jsonschema
        import yaml
    except ImportError:
        raise PackagingError("check necesita jsonschema y pyyaml: pip install jsonschema pyyaml")
    schemas = load_schemas(args.schemas)
    validator_cls = jsonschema.Draft7Validator

    # Sin opciones, se comprueba todo: bucket/roost.json y las plantillas de winget.
    everything = not args.scoop and not args.winget
    problems = []
    scoop = args.scoop or (str(BUCKET_MANIFEST) if everything else None)
    if scoop:
        found = check_scoop(scoop, schemas, validator_cls)
        print(f"Scoop {scoop}: {'bien' if not found else f'{len(found)} problema(s)'}")
        if json.loads(pathlib.Path(scoop).read_text(encoding="utf-8"))["architecture"]["64bit"]["hash"].strip("0") == "":
            print("  (el hash es el marcador: aun no apunta a ninguna release)")
        problems += found

    winget = args.winget
    if everything:
        # Las plantillas, rellenas con valores de prueba (el hash no puede ser todo cifras: YAML lo
        # leeria como un numero).
        winget = str(render_winget("0.0.1", "AB" * 32, "2026-01-01", tempfile.mkdtemp(prefix="roost-winget-")))
    if winget:
        found = check_winget(winget, schemas, validator_cls, yaml)
        print(f"winget {winget}: {'bien' if not found else f'{len(found)} problema(s)'}")
        problems += found

    for p in problems:
        print(f"  - {p}")
    return 1 if problems else 0


# ---------------------------------------------------------------- main

def main(argv=None):
    parser = argparse.ArgumentParser(description="Paquetes de Roost para Scoop y winget.")
    sub = parser.add_subparsers(dest="command", required=True)

    def release_args(p):
        p.add_argument("--version", required=True, help="Version de la release (0.1.0 o v0.1.0).")
        p.add_argument("--sha256", help="SHA-256 del zip de Windows.")
        p.add_argument("--sums", help="SHA256SUMS.txt de la release, del que se saca el SHA-256.")

    p = sub.add_parser("scoop", help="Actualiza bucket/roost.json.")
    release_args(p)
    p.add_argument("--manifest", default=str(BUCKET_MANIFEST), help="Manifiesto de Scoop (por defecto, bucket/roost.json).")
    p.add_argument("--force", action="store_true", help="Deja bajar de version.")
    p.set_defaults(func=cmd_scoop)

    p = sub.add_parser("winget", help="Genera los manifiestos de winget.")
    release_args(p)
    p.add_argument("--release-date", help="Fecha de la release, AAAA-MM-DD (por defecto, hoy en UTC).")
    p.add_argument("--out", required=True, help="Carpeta de salida (dentro va manifests/r/Roost/Roost/<version>).")
    p.set_defaults(func=cmd_winget)

    p = sub.add_parser("check", help="Valida los manifiestos contra los esquemas oficiales.")
    p.add_argument("--scoop", help="Manifiesto de Scoop. Sin --scoop ni --winget: bucket/roost.json y las plantillas.")
    p.add_argument("--winget", help="Carpeta con los manifiestos de winget de una version.")
    p.add_argument("--schemas", help="Carpeta con los esquemas (si faltan, se bajan ahi).")
    p.set_defaults(func=cmd_check)

    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except PackagingError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
