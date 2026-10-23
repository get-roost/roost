#!/usr/bin/env python3
"""Las novedades de una version, sacadas de CHANGELOG.md, para las notas de la release.

    python3 build/changelog-notes.py 0.1.3 [--changelog CHANGELOG.md]

Busca el titulo «## <version>» (con o sin «v», y con lo que venga detras de una raya: «## 0.1.3
— 24-oct-2026») y saca tal cual sus lineas hasta el siguiente «## ». Es la misma regla que
Changelog.Parse (Roost.Core/Updates). Lo usa .github/workflows/release.yml.

Si la version no tiene seccion, o no tiene ningun punto («- …»), sale con 1 y un error que
GitHub Actions enseña en el resumen del job: no se publica una release sin sus novedades.
Solo biblioteca estandar.
"""

import argparse
import pathlib
import sys

DASHES = "—–"  # raya y semirraya


def normalize(version):
    """«v0.1.3+abc» -> «0.1.3»."""
    return version.strip().lstrip("vV").split("+", 1)[0]


def heading_version(line):
    """La version de un «## 0.1.3 — fecha», o None si la linea no es un titulo de version."""
    if not line.startswith("## "):
        return None
    title = line[3:].strip()
    cut = min((title.find(d) for d in DASHES if d in title), default=-1)
    version = title if cut < 0 else title[:cut]
    return normalize(version), (title[cut + 1:].strip() if cut >= 0 else "")


def section(markdown, version):
    """(fecha, lineas) de la seccion de la version, o None si no esta."""
    wanted = normalize(version)
    lines = markdown.replace("\r\n", "\n").split("\n")
    for i, line in enumerate(lines):
        found = heading_version(line.rstrip())
        if not found or found[0] != wanted:
            continue
        body = []
        for following in lines[i + 1:]:
            if following.startswith("## "):
                break
            body.append(following.rstrip())
        while body and not body[0].strip():
            body.pop(0)
        while body and not body[-1].strip():
            body.pop()
        return found[1], body
    return None


def main(argv=None):
    # Las novedades llevan tildes, rayas y flechas: UTF-8 aunque la consola sea otra cosa.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("version", help="La version (0.1.3 o v0.1.3).")
    parser.add_argument("--changelog", default=str(pathlib.Path(__file__).resolve().parent.parent / "CHANGELOG.md"))
    args = parser.parse_args(argv)

    path = pathlib.Path(args.changelog)
    if not path.is_file():
        print(f"::error::No esta {path}: las notas de la release salen de ahi.", file=sys.stderr)
        return 1
    version = normalize(args.version)
    found = section(path.read_text(encoding="utf-8-sig"), version)
    if found is None:
        print(f"::error file=CHANGELOG.md::CHANGELOG.md no tiene la seccion de la {version}: "
              f"añade un «## {version} — <fecha>» con sus novedades («- …») antes de publicar.", file=sys.stderr)
        return 1
    date, body = found
    if not any(line.startswith("- ") for line in body):
        print(f"::error file=CHANGELOG.md::La seccion «## {version}» de CHANGELOG.md no tiene ningun punto («- …»): "
              "escribe sus novedades antes de publicar.", file=sys.stderr)
        return 1
    if "preparaci" in date.lower():
        print(f"::warning file=CHANGELOG.md::La seccion de la {version} dice «{date}»: pon la fecha de la release.", file=sys.stderr)
    sys.stdout.write("\n".join(body) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
