# Roost.Core

El núcleo de [Roost](https://github.com/get-roost/roost), el entorno de desarrollo PHP para Windows y Linux que audita lo que instala. Es lo que usan por igual la CLI (`roost`), el daemon y la app de escritorio:

- detección de PHP, MySQL y MariaDB instalados (`Scanning`): Roost, Laragon, XAMPP, WAMP, Herd, scoop, chocolatey, rutas de distro, Homebrew y el PATH;
- auditoría de EOL y CVE contra un dataset de avisos (`Audit`), sin red;
- instalación verificada por SHA-256 de PHP, nginx, MariaDB y MySQL (`Install`);
- `roost.toml` (`Config`), el supervisor de procesos (`Services`) y la generación del stack: `php.ini`, `nginx.conf`, `my.ini`, sitios `.test` y su CA local (`Stack`, `Sites`);
- las rutas de Roost (`Platform/RoostPaths`).

No hace falta para usar Roost: este paquete es para compilar extensiones (normalmente a través de [Roost.Extensibility](https://www.nuget.org/packages/Roost.Extensibility)). Una extensión no debería llevarse su propia copia: Roost le comparte la suya.

La API todavía no es estable: mientras la versión sea 0.x, cualquier versión menor puede romperla. El contrato de las extensiones lleva su propia versión (`ExtensionApi.Current`).

Licencia MIT.
