#!/usr/bin/env python3
"""La parte de la acción «Roost Audit» que no es shell.

    python3 roost_audit.py install [--version latest|0.1.0] [--repository dueño/repo]
    python3 roost_audit.py summary --json audit.json --exit-code 1 [--fail-on-blocking true]

- `install` baja de las releases de Roost la build del runner (Linux x64 o Windows x64), verifica
  su SHA-256 contra el SHA256SUMS.txt de la misma release, la descomprime y pone su carpeta en el
  PATH del job.
- `summary` convierte el JSON de `roost audit --json` en el resumen del job (Markdown) y en
  anotaciones, y deja en las salidas del paso los contadores del semáforo y si hay algo bloqueante.
  El filtro de la entrada `only` lo hace `roost audit --only` (las imágenes de los runners traen
  su propio MySQL, y sin filtro un MySQL 8.0, EOL, de la imagen haría fallar la CI de cualquier
  proyecto PHP): aquí solo se cuenta lo que dice el JSON.

Usa lo de siempre de GitHub Actions (RUNNER_OS, RUNNER_ARCH, RUNNER_TEMP, GITHUB_OUTPUT,
GITHUB_PATH, GITHUB_STEP_SUMMARY), y sin ello también funciona en local. Solo biblioteca estándar
(Python 3.8+), para que valga en cualquier runner sin instalar nada.
"""

import argparse
import hashlib
import html
import json
import os
import pathlib
import platform
import re
import shutil
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

USER_AGENT = "roost-audit-action"
TITLE = "Roost Audit"

# (RUNNER_OS, RUNNER_ARCH) -> (plataforma del asset, extensión, ejecutable)
PLATFORMS = {
    ("Linux", "X64"): ("linux-x64", ".tar.gz", "roost"),
    ("Windows", "X64"): ("win-x64", ".zip", "roost.exe"),
}
VERSION = re.compile(r"^v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$")
SHA256 = re.compile(r"^[0-9a-fA-F]{64}$")


class Failure(Exception):
    """Un error para el usuario: sale como anotación ::error:: y el paso falla."""


# ---------------------------------------------------------------- utilidades de Actions

def escape_data(text):
    return str(text).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(text):
    return escape_data(text).replace(":", "%3A").replace(",", "%2C")


def annotate(level, message):
    print(f"::{level} title={escape_property(TITLE)}::{escape_data(message)}", flush=True)


def set_outputs(**values):
    target = os.environ.get("GITHUB_OUTPUT")
    lines = "".join(f"{k.replace('_', '-')}={v}\n" for k, v in values.items())
    if target:
        with open(target, "a", encoding="utf-8") as f:
            f.write(lines)
    else:
        sys.stdout.write(lines)


def add_to_path(folder):
    target = os.environ.get("GITHUB_PATH")
    if target:
        with open(target, "a", encoding="utf-8") as f:
            f.write(f"{folder}\n")


# ---------------------------------------------------------------- install

def runner_platform(runner_os, runner_arch):
    runner_os = runner_os or {"Darwin": "macOS"}.get(platform.system(), platform.system())
    runner_arch = runner_arch or {"AMD64": "X64", "x86_64": "X64", "aarch64": "ARM64", "arm64": "ARM64"}.get(
        platform.machine(), platform.machine().upper())
    if runner_os == "macOS":
        raise Failure("Roost todavia no publica builds para macOS. Usa un runner ubuntu-* (Linux x64) o windows-* (Windows x64).")
    if (runner_os, runner_arch) not in PLATFORMS:
        raise Failure(f"No hay build de Roost para {runner_os} {runner_arch}: de momento, solo Linux x64 y Windows x64.")
    return runner_os, runner_arch, PLATFORMS[(runner_os, runner_arch)]


def open_url(url, timeout=120):
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    return urllib.request.urlopen(request, timeout=timeout)


def resolve_version(requested, releases):
    """«latest» es la última release estable: a la que redirige github.com/<repo>/releases/latest."""
    requested = (requested or "latest").strip()
    if requested.lower() != "latest":
        match = VERSION.match(requested)
        if not match:
            raise Failure(f"'{requested}' no es una version de Roost: usa latest, 0.1.0 o v0.1.0.")
        return match.group(1)
    try:
        with open_url(f"{releases}/latest", timeout=60) as response:
            final = response.geturl()
    except urllib.error.HTTPError as e:
        if e.code == 404:
            raise Failure(f"No hay ninguna release de Roost publicada en {releases}.")
        raise Failure(f"No se pudo saber cual es la ultima release de Roost ({releases}/latest): HTTP {e.code}.")
    except (urllib.error.URLError, OSError) as e:
        raise Failure(f"No se pudo saber cual es la ultima release de Roost ({releases}/latest): {e}.")
    match = re.search(r"/releases/tag/([^/?#]+)$", final)
    version = VERSION.match(urllib.parse.unquote(match.group(1))) if match else None
    if not version:
        raise Failure(f"La ultima release de {releases} no tiene una etiqueta v<version> ({final}).")
    return version.group(1)


def download(url, dest):
    """Descarga con tres intentos; un 404 no se reintenta."""
    tmp = dest.with_name(dest.name + ".part")
    for attempt in range(1, 4):
        try:
            with open_url(url) as response, open(tmp, "wb") as f:
                shutil.copyfileobj(response, f)
            tmp.replace(dest)
            return
        except urllib.error.HTTPError as e:
            if e.code == 404:
                raise Failure(f"No existe {url} (404): la release no trae ese fichero.")
            error = f"HTTP {e.code}"
        except (urllib.error.URLError, OSError) as e:
            error = str(e)
        if attempt == 3:
            raise Failure(f"No se pudo descargar {url}: {error}.")
        time.sleep(3 * attempt)


def expected_sha(sums, file_name):
    for line in sums.read_text(encoding="utf-8-sig").splitlines():
        parts = line.strip().split(None, 1)
        if len(parts) == 2 and parts[1].lstrip("*") == file_name and SHA256.match(parts[0]):
            return parts[0].lower()
    raise Failure(f"{sums.name} no trae el SHA-256 de {file_name}.")


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def inside(root, member):
    target = (root / member).resolve()
    return target == root or root in target.parents


def extract(archive, dest):
    if dest.exists():
        shutil.rmtree(dest)
    dest.mkdir(parents=True)
    root = dest.resolve()
    if archive.name.endswith(".zip"):
        with zipfile.ZipFile(archive) as z:
            for name in z.namelist():
                if not inside(root, name):
                    raise Failure(f"{archive.name} trae una ruta fuera de su carpeta: {name}")
            z.extractall(dest)
    else:
        with tarfile.open(archive, "r:gz") as t:
            if hasattr(tarfile, "data_filter"):
                t.extractall(dest, filter="data")
            else:
                for member in t.getmembers():
                    if not inside(root, member.name) or member.issym() or member.islnk():
                        raise Failure(f"{archive.name} trae una entrada que no se puede extraer sin riesgo: {member.name}")
                t.extractall(dest)


def find_executable(folder, name):
    """En la raíz, como manda el contrato de las releases; si viniera en una carpeta, se busca un nivel abajo."""
    direct = folder / name
    if direct.is_file():
        return direct
    nested = [p for p in folder.glob(f"*/{name}") if p.is_file()]
    if len(nested) == 1:
        return nested[0]
    raise Failure(f"El paquete de Roost no trae {name}.")


def cmd_install(args):
    parse_only(args.only)
    runner_os, runner_arch, (plat, ext, exe_name) = runner_platform(
        args.os or os.environ.get("RUNNER_OS"), args.arch or os.environ.get("RUNNER_ARCH"))
    releases = (args.base_url or f"https://github.com/{args.repository}/releases").rstrip("/")
    version = resolve_version(args.version, releases)
    asset = f"roost-{version}-{plat}{ext}"
    base = f"{releases}/download/v{version}"

    work = pathlib.Path(os.environ.get("RUNNER_TEMP") or tempfile.gettempdir()) / "roost-audit" / version
    work.mkdir(parents=True, exist_ok=True)
    print(f"Roost {version} para {runner_os} {runner_arch}: {base}/{asset}", flush=True)

    sums = work / "SHA256SUMS.txt"
    download(f"{base}/SHA256SUMS.txt", sums)
    expected = expected_sha(sums, asset)
    archive = work / asset
    if not archive.exists() or sha256_of(archive) != expected:
        download(f"{base}/{asset}", archive)
    actual = sha256_of(archive)
    if actual != expected:
        archive.unlink()
        raise Failure(f"El SHA-256 de {asset} no cuadra con SHA256SUMS.txt (esperado {expected}, descargado {actual}). No se usa.")
    print(f"SHA-256 verificado: {actual}", flush=True)

    folder = work / plat
    extract(archive, folder)
    exe = find_executable(folder, exe_name)
    if os.name != "nt":
        exe.chmod(exe.stat().st_mode | 0o755)
    add_to_path(exe.parent)
    set_outputs(version=version, dir=exe.parent, exe=exe.as_posix(), json=(work / "roost-audit.json").as_posix())
    print(f"roost listo en {exe.parent}", flush=True)
    return 0


# ---------------------------------------------------------------- summary

KINDS = {"Php": "PHP", "MySql": "MySQL", "MariaDb": "MariaDB", "Nginx": "nginx", "Apache": "Apache"}
# Lo que admite la entrada `only`: el `kind` del JSON en minúsculas.
ONLY_KINDS = {"php": "PHP", "mysql": "MySQL", "mariadb": "MariaDB"}
DOTS = {"red": "🔴", "amber": "🟠", "green": "🟢"}


def cell(text):
    return str(text if text is not None else "").replace("|", "\\|").replace("\r", "").replace("\n", " ")


def code(text):
    text = cell(text)
    return f"`{text}`" if text and "`" not in text else text


def component_name(c):
    return f"{KINDS.get(c.get('kind'), c.get('kind') or '?')} {c.get('version') or '?'}"


def verdict(f):
    return f"{f.get('verdict', '?')}{'?' if f.get('uncertain') else ''}"


def parse_only(text):
    """«php, mysql» -> {"php", "mysql"}; vacío -> None (cuentan todos)."""
    names = {p.strip().lower() for p in (text or "").replace(";", ",").split(",") if p.strip()}
    unknown = names - set(ONLY_KINDS)
    if unknown:
        raise Failure(f"only solo admite {', '.join(ONLY_KINDS)} (separados por comas), no: {', '.join(sorted(unknown))}.")
    return names or None


def render_summary(report, exit_code, fail_on_blocking, roost_version, platform_label):
    findings = report.get("findings") or []
    # Con --only, roost audit ya ha filtrado: lo que trae el JSON es lo que cuenta.
    only = [k for k in (report.get("only") or []) if k in ONLY_KINDS]
    red = sum(1 for f in findings if f.get("color") == "red")
    amber = sum(1 for f in findings if f.get("color") == "amber")
    green = sum(1 for f in findings if f.get("color") == "green")
    blocking = bool(report.get("blocking", exit_code == 1))
    worst = "red" if red else "amber" if amber else "green"

    md = [f"### {DOTS[worst]} {TITLE}: {red} en rojo, {amber} en ambar, {green} en verde", ""]
    if blocking and fail_on_blocking:
        md.append("**El paso falla:** hay al menos un componente VULNERABLE o EOL con certeza (`roost audit` sale con 1).")
    elif blocking:
        md.append("**Hay componentes VULNERABLE o EOL con certeza** (`roost audit` sale con 1), pero el paso no falla porque `fail-on-blocking` es `false`.")
    elif amber:
        md.append("Nada bloqueante. Lo ambar no hace fallar el paso: Roost no puede asegurar si esta parcheado "
                  "(por ejemplo, un paquete de distro, que puede llevar el parche por backport aunque su numero parezca viejo).")
    else:
        md.append("Nada bloqueante.")
    if only:
        names = ", ".join(ONLY_KINDS[k] for k in ONLY_KINDS if k in only)
        md.append(f"Solo cuentan {names} (`only`): {report.get('skipped') or 0} componente(s) mas del runner quedan fuera.")
    md.append("")

    if not findings:
        md += ["Roost no ha encontrado ningun PHP, MySQL ni MariaDB en el runner." if not only
               else "Roost no ha encontrado en el runner ningun componente de los que cuentan.", ""]
    else:
        md += ["| | Componente | Origen | Veredicto | Detalle | Ruta |", "|---|---|---|---|---|---|"]
        for f in findings:
            c = f.get("component") or {}
            md.append(f"| {DOTS.get(f.get('color'), '')} | {cell(component_name(c))} | {cell(c.get('source'))} | "
                      f"{cell(verdict(f))} | {cell(f.get('headline'))} | {code(c.get('executablePath'))} |")
        md.append("")

    for f in findings:
        if f.get("color") == "green":
            continue
        c = f.get("component") or {}
        md += ["<details>", f"<summary>{DOTS.get(f.get('color'), '')} {html.escape(component_name(c))} "
               f"({html.escape(str(c.get('source') or '?'))}): {html.escape(verdict(f))}</summary>", ""]
        if f.get("headline"):
            md += [cell(f["headline"]), ""]
        for a in f.get("advisories") or []:
            aliases = f" ({', '.join(a['aliases'])})" if a.get("aliases") else ""
            cvss = f" · CVSS {a['cvss']:.1f}" if isinstance(a.get("cvss"), (int, float)) else ""
            kev = " · KEV (explotacion activa)" if a.get("kev") else ""
            md.append(f"- **{cell(a.get('id'))}**{cell(aliases)}{cvss}{kev}: {cell(a.get('title'))}")
        mitigations = f.get("mitigations") or []
        if mitigations:
            md += ["", "Mitigaciones mientras tanto (no sustituyen a actualizar):"]
            for m in mitigations:
                setting = "{} = {}".format(m.get("ini"), m.get("value"))
                note = f": {cell(m['note'])}" if m.get("note") else ""
                md.append(f"- {code(setting)}{note}")
        for note in f.get("notes") or []:
            md.append(f"- {cell(note)}")
        if c.get("versionRaw"):
            md += ["", f"Version detectada: {code(c['versionRaw'])}"]
        md += ["", "</details>", ""]

    dataset = report.get("dataset") or {}
    generated = str(dataset.get("generated") or "")[:10]
    # origin: "feed" (el feed firmado de roost-catalog), "embedded" (el que va dentro de Roost) o "file".
    origin = {"feed": "feed firmado", "embedded": "dataset empaquetado", "file": "fichero sin firma"}.get(
        dataset.get("origin"), f"dataset {dataset.get('schemaVersion', '?')}")
    footer = [f"Roost {roost_version}" if roost_version else "Roost", platform_label,
              f"{origin} del {generated or '?'}", dataset.get("note"), "evaluado en local"]
    md.append("<sub>" + " · ".join(html.escape(p) for p in footer if p) + "</sub>")
    stats = {"red": red, "amber": amber, "green": green, "blocking": blocking, "findings": findings}
    return "\n".join(md) + "\n", stats


def render_failure(exit_code, raw):
    reason = ("no pudo cargar el dataset de avisos (codigo 2)" if exit_code == 2
              else f"termino con el codigo {exit_code} sin un JSON valido")
    md = [f"### ⚠️ {TITLE}: no se pudo auditar", "", f"`roost audit --json` {reason}."]
    if raw.strip():
        md += ["", "```", "\n".join(raw.strip().splitlines()[:30]), "```"]
    return "\n".join(md) + "\n"


def cmd_summary(args):
    path = pathlib.Path(args.json)
    raw = path.read_text(encoding="utf-8-sig", errors="replace") if path.exists() else ""
    fail = args.fail_on_blocking.strip().lower() != "false"
    label = args.platform or " ".join(p for p in (os.environ.get("RUNNER_OS"), os.environ.get("RUNNER_ARCH")) if p)
    try:
        report = json.loads(raw)
        if not isinstance(report, dict) or "findings" not in report:
            raise ValueError("sin findings")
    except ValueError:
        markdown, stats = render_failure(args.exit_code, raw), None
    else:
        markdown, stats = render_summary(report, args.exit_code, fail, args.roost_version, label)

    target = args.out or os.environ.get("GITHUB_STEP_SUMMARY")
    if target:
        with open(target, "a", encoding="utf-8") as f:
            f.write(markdown)
    else:
        sys.stdout.write(markdown)

    if stats is None:
        set_outputs(blocking="", red="", amber="", green="")
        return 0
    set_outputs(blocking=str(stats["blocking"]).lower(), red=stats["red"], amber=stats["amber"], green=stats["green"])

    # Anotaciones: lo rojo como error (o aviso, si no se falla), lo ámbar como nota. Diez de cada, como mucho.
    shown = {"red": 0, "amber": 0}
    for f in stats["findings"]:
        color = f.get("color")
        if color not in shown or shown[color] >= 10:
            continue
        shown[color] += 1
        c = f.get("component") or {}
        level = ("error" if fail else "warning") if color == "red" else "notice"
        annotate(level, f"{component_name(c)} ({c.get('source')}, {c.get('executablePath')}): {verdict(f)}. {f.get('headline') or ''}".strip())
    return 0


# ---------------------------------------------------------------- main

def main(argv=None):
    # Los logs de Actions son UTF-8; en Windows, sin esto, Python escribiria en la pagina de codigos ANSI.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass

    parser = argparse.ArgumentParser(description="Instala Roost en el runner y resume roost audit.")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("install", help="Baja, verifica y descomprime la build de Roost del runner.")
    p.add_argument("--version", default="latest", help="latest (la ultima estable), 0.1.0 o v0.1.0.")
    p.add_argument("--repository", default="get-roost/roost", help="Repositorio de las releases.")
    p.add_argument("--base-url", default=os.environ.get("ROOST_AUDIT_BASE_URL"),
                   help="URL de las releases en vez de https://github.com/<repo>/releases (para pruebas; "
                        "tambien con la variable ROOST_AUDIT_BASE_URL).")
    p.add_argument("--os", help="Linux o Windows (por defecto, RUNNER_OS).")
    p.add_argument("--arch", help="X64 (por defecto, RUNNER_ARCH).")
    p.add_argument("--only", default="", help="Solo se valida (php, mysql, mariadb): asi un error sale antes de bajar nada.")
    p.set_defaults(func=cmd_install)

    p = sub.add_parser("summary", help="Resume el JSON de roost audit --json.")
    p.add_argument("--json", required=True, help="Fichero con la salida de roost audit --json.")
    p.add_argument("--exit-code", type=int, required=True, help="Codigo de salida de roost audit --json.")
    p.add_argument("--fail-on-blocking", default="true", help="true o false.")
    p.add_argument("--roost-version", default="", help="Version de Roost usada, para el pie.")
    p.add_argument("--platform", help="Plataforma, para el pie (por defecto, RUNNER_OS y RUNNER_ARCH).")
    p.add_argument("--out", help="Fichero Markdown (por defecto, GITHUB_STEP_SUMMARY; si no, la salida).")
    p.set_defaults(func=cmd_summary)

    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except Failure as e:
        annotate("error", str(e))
        return 1


if __name__ == "__main__":
    sys.exit(main())
