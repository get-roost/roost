# Roost Audit

Una GitHub Action que pasa [`roost audit`](https://github.com/get-roost/roost#roost-audit) en el runner: busca los PHP, MySQL y MariaDB instalados, los evalúa contra el dataset de avisos y fechas de fin de soporte que va dentro de Roost y deja el semáforo en el resumen del job. Si algo está VULNERABLE o en EOL *con certeza*, el paso falla.

Baja la build de Roost de las [releases](https://github.com/get-roost/roost/releases) (Linux x64 o Windows x64), verifica su SHA-256 contra el `SHA256SUMS.txt` de la misma release y no usa la red para nada más: la evaluación es local.

## Uso

En un proyecto PHP, después de [`shivammathur/setup-php`](https://github.com/shivammathur/setup-php):

```yaml
name: CI

on: [push, pull_request]

jobs:
  tests:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v7

      - uses: shivammathur/setup-php@v2
        with:
          php-version: '8.3'

      # Falla si el PHP del runner está fuera de soporte o le afecta un aviso conocido.
      - uses: get-roost/roost/actions/audit@v0.1.0
        with:
          only: php

      - run: composer install --no-progress
      - run: vendor/bin/phpunit
```

Con una matriz de versiones, sirve para enterarse de que la CI sigue probando una rama EOL:

```yaml
    strategy:
      matrix:
        php: ['7.4', '8.2', '8.3', '8.4']
    steps:
      - uses: shivammathur/setup-php@v2
        with:
          php-version: ${{ matrix.php }}
      - uses: get-roost/roost/actions/audit@v0.1.0
        with:
          only: php
          fail-on-blocking: ${{ matrix.php != '7.4' }}   # la 7.4 se prueba a sabiendas
```

Usa una etiqueta (`@v0.1.0`) o un commit, no `@main`. La versión de la acción y la de Roost que baja son independientes: `version` elige la de Roost, y con ella el dataset de avisos.

## Entradas

| Entrada | Por defecto | Qué hace |
|---|---|---|
| `version` | `latest` | Versión de Roost: `latest` (la última release estable), `0.1.0` o `v0.1.0`. |
| `fail-on-blocking` | `true` | `true`: el paso falla si hay algo VULNERABLE o EOL con certeza (el código `1` de `roost audit`). `false`: lo deja en un aviso. Lo ámbar no hace fallar nunca. |
| `only` | *(vacío: todo)* | Qué tipos cuentan para el resumen y para fallar, separados por comas: `php`, `mysql`, `mariadb`. |
| `args` | *(vacío)* | Argumentos extra para `roost audit`, separados por espacios; por ejemplo, `--dataset ruta/advisories.json`. `--json` ya lo pone la acción. |
| `repository` | `get-roost/roost` | Repositorio del que se bajan las releases. |

## Salidas

| Salida | Qué es |
|---|---|
| `exit-code` | El código de `roost audit`: `0` nada bloqueante, `1` algo VULNERABLE o EOL con certeza, `2` no se pudo cargar el dataset. Es el de verdad, sin el filtro de `only`. |
| `blocking` | `true` si hay algo bloqueante entre lo que cuenta. |
| `red`, `amber`, `green` | Cuántos componentes de cada color, entre lo que cuenta. |
| `json` | Ruta del fichero con la salida de `roost audit --json`, para procesarla en otro paso. |
| `version` | La versión de Roost usada. |
| `roost` | La ruta del ejecutable. Su carpeta queda además en el `PATH`, así que los pasos siguientes pueden llamar a `roost` directamente. |

## Qué audita (y por qué conviene `only: php`)

Audita **el runner entero**, no el proyecto: el PHP que haya puesto `setup-php` y todo lo que traiga la imagen. Las imágenes de GitHub traen su propio MySQL 8.0, que para Roost está en EOL desde abril de 2026; sin `only`, ese MySQL que tu proyecto no usa haría fallar el job.

En **Linux**, lo que hay en `/usr/bin`, `/usr/local/bin` y `/opt` sale con origen `system` y en ámbar, como cualquier paquete de distro: puede llevar el parche por backport aunque su número de versión parezca viejo, y Roost no lo puede asegurar. `setup-php` instala en Ubuntu paquetes de distro, así que en Linux la acción enseña el semáforo, pero no falla por el PHP. En **Windows**, `setup-php` pone los zips oficiales de windows.php.net en `C:\tools\php`, que sí se evalúan por número: ahí la acción puede ponerse en rojo.

macOS todavía no tiene build de Roost: la acción falla con un error claro. Hace falta `bash` y Python 3.8 o posterior, que ya traen los runners de GitHub en Linux y en Windows.

## Licencia

MIT, como Roost.
