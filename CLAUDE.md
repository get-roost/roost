# CLAUDE.md

Este fichero orienta a Claude Code (claude.ai/code) cuando trabaja con el código de este repositorio.

## Reglas del proyecto

- **Todo en castellano** (regla permanente del usuario): respuestas, preguntas, resúmenes y cualquier documento que escribas, incluido este fichero.

## Qué es esto

Roost implementa el producto descrito en `ROOST.md`, el documento de diseño. **Es interno y no está en este repo**: en la máquina del equipo está en la carpeta superior, `../ROOST.md`, que es otro repo, privado. Las referencias «ROOST.md §…» del código y de este fichero son a él. Hoy es un CLI:

- `roost scan` inventaría las instalaciones de PHP / MySQL / MariaDB de la máquina (Roost, Laragon, XAMPP, WAMP, Herd, scoop, chocolatey, Program Files, rutas de distro, Homebrew, PATH).
- `roost audit` evalúa cada instalación, en local, contra el feed firmado de avisos y fechas EOL (`roost-catalog`, fase 0). Lo baja como mucho una vez al día; sin él, usa el dataset empaquetado en el binario. `roost catalog status / update` enseña y actualiza el feed.
- `roost php list / install / remove / use` (fase 1, hito 1, solo Windows) instala PHP oficiales de windows.php.net en la raíz de Roost y fija el PHP global.
- `roost up / down / status` (fase 1, hito 2, solo Windows) sirve el PHP global con nginx en `http://localhost/` a través de un daemon de usuario con supervisor. La primera vez instala nginx (o Apache, si es el elegido).
- `roost web use <nginx|apache>` elige el servidor web: lo instala si falta, lo guarda en `[web] server` y, si el stack está en marcha, lo rearranca (`Commands/Stack/WebCommands.cs`; `WebServerSetup.EnsureAsync` es la instalación que comparte con `up`).
- `roost db list / install / remove / use / create` (fase 1, hito 3, solo Windows) instala y elige la base de datos global: MariaDB (cualquier rama estable) o MySQL 8.4, 8.0 y 5.7. `roost up` la arranca antes que PHP.
- `roost site add / remove / list` y `roost cert trust / renew` (fase 1, hito 4, solo Windows) sirven cada proyecto en `https://<nombre>.test/`. Cada sitio tiene su vhost, su certificado de la CA local y, opcionalmente, su propio PHP. Lo resuelve el `hosts` del sistema, que edita `roost-helper.exe` elevado (UAC).
- `roost site clone / pull / branch / git` y `roost git install / status` (fase 2, git al estilo Plesk; de momento, repos públicos y locales, solo en la CLI): sitios desde un repositorio, con MinGit verificado y un entorno aislado.
- `roost restart <servicio>` reinicia el servidor web (`nginx` o `apache`), el pool de un PHP (`php-8.4`) o la base de datos sin tocar el resto.
- `roost shims install / remove / status`, `roost php which` y `roost php use <version> --here` (fase 2, hito 1): `php` en la terminal usa el PHP que toca en cada carpeta.
- `roost dns install / remove / status` (fase 2, hito 3, solo Windows): los `.test` los responde un DNS local dentro de roostd y, con una regla NRPT (un solo UAC), dar de alta o de baja un sitio ya no pide permiso. Es opcional (`[sites] resolver`): por defecto sigue el `hosts`.
- Extensiones (fase 2, hito 2, ROOST.md §12.4): lo que haya en `bin/extensions/` puede añadir servicios al stack, avisos y scanners a la auditoría, comandos a la CLI y pantallas a la app. `roost extensions` dice cuáles cargan. Es la base del futuro Roost Pro; de momento no hay ninguna.
- `roost update` y el aviso de la app: miran en las releases de GitHub si hay una Roost nueva y dicen cómo actualizar según cómo se instaló (Scoop, winget, un zip, compilada). Importa porque nginx, MySQL y el dataset de avisos van fijados en el binario. Solo avisa: no descarga ni sustituye nada (eso llegará con un instalador).
- **App de escritorio** (`Roost.Desktop`, Avalonia, solo Windows por ahora): la ventana estilo Laragon del mockup aprobado por el usuario (la referencia de diseño de todas las pantallas; el enlace está en la memoria del proyecto), con bandeja del sistema. Hace lo mismo que la CLI, sobre Core y el daemon.

La fase 1 se hace **Windows primero** (Linux necesita builds propios de PHP en CI) y, de momento, con el **catálogo leído directamente de upstream** en vez del feed firmado `roost-catalog`. Aún no hay base de datos por proyecto, varias bases de datos a la vez, plantillas (`roost new`) ni adopción de Laragon. Antes de proponer cambios de arquitectura, lee ROOST.md para entender la intención y las decisiones ya tomadas (Apéndice A).

## Comandos

Hace falta el SDK de .NET 10: todos los proyectos apuntan a `net10.0` y la solución usa el formato XML `.slnx`.

```sh
dotnet build Roost.slnx
dotnet test Roost.slnx
dotnet test tests/Roost.Core.Tests --filter "FullyQualifiedName~AuditEvaluatorTests.Php_8_3_18_Es_Vulnerable"   # un solo test
dotnet run --project src/Roost.Cli -- scan [--json]
dotnet run --project src/Roost.Cli -- audit [--json] [--offline] [--dataset ruta/a/advisories.json] ; catalog status | update
dotnet run --project src/Roost.Catalog -- generate --catalog catalog --out <dir> --unsigned   # el feed, sin firmar (va a la red)
dotnet run --project src/Roost.Cli -- php list | install <rama|version> [--ts] [--arch x86] | remove <nombre|version|rama> | use <version>
dotnet run --project src/Roost.Cli -- db list | install <motor@version> | remove <motor@version> | use <motor@version|none> | create <nombre>
dotnet run --project src/Roost.Cli -- site add <nombre> [--path] [--root] [--php <version|global>] | remove <nombre> | list
dotnet run --project src/Roost.Cli -- site clone <url> [--name] [--branch] [--path] [--php] [--yes] [--json] | pull <sitio> | branch <sitio> [<rama>] | git <sitio> [--check] [--adopt] [--json]
dotnet run --project src/Roost.Cli -- git install | status [--json]
ROOST_TEST_GIT=1 dotnet test tests/Roost.Core.Tests --filter GitIntegrationTests   # git de verdad (el del PATH, o la ruta de un git.exe), con repos file://
dotnet run --project src/Roost.Cli -- cert trust | renew
dotnet run --project src/Roost.Cli -- up | down | status [--json] | restart <servicio>
dotnet run --project src/Roost.Cli -- shims install | remove | status ; php which [--dir] ; php use <version> --here
dotnet run --project src/Roost.Cli -- update [--json]       # ¿hay una Roost nueva? (con ROOST_UPDATE_URL, contra un servidor propio)
dotnet run --project src/Roost.Cli -- dns install | remove | status
dotnet publish src/Roost.Shim -c Release -r win-x64 -o <carpeta>   # el shim con NativeAOT (necesita vswhere en el PATH)
dotnet run --project src/Roost.Desktop                     # la app; con ROOST_HOME para no tocar C:\roost
dotnet test tests/Roost.Desktop.Tests                       # vistas en headless; capturas en bin/.../TestResults/snapshots
```

- **No toques el `hosts` real ni el almacén de certificados del usuario sin que lo pida.** El `hosts` pide UAC; `cert trust` enseña un diálogo de Windows y añade una raíz de confianza. Para probar los sitios:
  - define `ROOST_HOSTS_FILE` con un fichero de pruebas, que Roost escribe directamente, sin helper ni UAC (el helper elevado nunca lee esa variable);
  - comprueba el HTTPS con `curl.exe --ssl-no-revoke --cacert <ROOST_HOME>\certs\ca\roost-ca.crt --resolve <nombre>.test:<puerto>:127.0.0.1 https://<nombre>.test:<puerto>/`.

  Sin `--ssl-no-revoke`, el curl de Windows (Schannel) falla con «revocation status is unknown», porque la CA local no publica lista de revocación. Los navegadores no la piden.

- Para probar a mano sin crear `C:\roost` ni `%APPDATA%\Roost`, define `ROOST_HOME` con una carpeta temporal: todo va debajo, y la caché a `ROOST_HOME\cache`. El daemon hereda la variable.
- Para probar el aviso de actualizaciones sin depender de GitHub, sirve un JSON con la forma de `/releases/latest` (`py -3 -m http.server <puerto> --bind 127.0.0.1`) y define `ROOST_UPDATE_URL=http://127.0.0.1:<puerto>/latest.json` (y `ROOST_HOME`): `roost update` lo consulta y deja la caché que leen `roost status` y la app. La caché recuerda la URL, así que al quitar la variable no se mezcla con la de GitHub. Mientras no haya ninguna release, la API real da 404 («aún no hay ninguna versión publicada»).
- Para probar `roost up` usa puertos libres (`[web] port` y `[db] port` en `ROOST_HOME\roost.toml`): en una máquina de desarrollo el 80, el 8080, el 3306 y el 3307 suelen estar cogidos (Laragon, `php -S`, otro MySQL, túneles). No pares lo que haya en ellos.
- Los zips grandes (MySQL 8.4, de unos 270 MB) se pueden copiar a `ROOST_HOME\cache\downloads` para no volver a descargarlos: el instalador los reutiliza si su SHA-256 cuadra.
- **No ejecutes `roost shims install` (ni la casilla de Ajustes) sin que el usuario lo pida:** cambia el PATH de su usuario. Para probar el shim, cópialo a mano a `ROOST_HOME\bin\shims\php.exe` y llámalo por su ruta completa: saca la raíz de su propia ubicación. En PowerShell 5.1 no lo pruebes con `-r "..."`: PowerShell se come las comillas dobles de los argumentos; usa un script `.php`.
- **No pongas la regla NRPT (`roost dns install`, el DNS local de Ajustes ni `roost-helper dns-enable`) sin que el usuario lo pida:** pide UAC y cambia la configuración de DNS del sistema. Para probar el DNS local, pon `[sites] resolver = "dns"` en el `roost.toml` de `ROOST_HOME`, arranca con `roost up` y pregúntale directamente: `Resolve-DnsName tienda.test -Server 127.53.0.1 -DnsOnly`. Sin regla puesta, `roost dns remove` no pide UAC: solo vuelve al hosts (al de pruebas, con `ROOST_HOSTS_FILE`).
- El publish con NativeAOT llama a `vswhere.exe`, que no está en el PATH de Git Bash: añade antes `C:\Program Files (x86)\Microsoft Visual Studio\Installer`.
- **La clave privada del feed no está en el repo** (es el secreto `ROOST_CATALOG_KEY` del workflow). Para probar el generador, `--unsigned`; para probar el cliente contra un feed, sírvelo en local y apunta `ROOST_CATALOG_URL` a él. Las pruebas del cliente usan su propia clave (`AdvisoryFeedTests`). Si cambias la clave, cambia `FeedSignature.TrustedKeys` en una versión de Roost *antes* que el secreto.
- **Antes de recompilar, `roost down`.** Con el stack en marcha, el daemon (`roost.exe daemon`) tiene bloqueados `roost.dll` y compañía en `bin/`, y el build falla al copiarlos.

Códigos de salida de `audit`: `0` nada bloqueante, `1` al menos un componente VULNERABLE o EOL *con certeza*, `2` no se pudo cargar el dataset, `3` opciones que no valen (`--only` con un tipo que no existe; también en `scan`). Con `--only php,mysql` (`ComponentFilter`, en Core) solo se evalúa eso, y el resumen, `blocking` y el código salen de lo filtrado; el JSON lo dice en `only` y `skipped`. La Action de auditoría le pasa su entrada `only` y no filtra ella. ROOST.md §4.5 describe otro esquema 0/1/2; manda el código.

## Versión, releases y NuGet

- **La versión vive solo en `<Version>` de `Directory.Build.props`**, que lleva además lo común de todos los proyectos (autor, licencia, repositorio, metadatos de NuGet). En tiempo de ejecución se lee de `AssemblyInformationalVersionAttribute` quitando lo que va tras `+`: `RoostVersion.Current` (Core, `Updates/`), que usan `roost --version`, los User-Agent de la CLI y de la app, Ajustes y el aviso de actualizaciones.
- **Cada versión cuenta lo que cambia en `CHANGELOG.md`** (raíz del repo, en castellano y para el usuario, no para quien programa).
  - Formato: una sección `## <versión> — <fecha>` con sus puntos `- **Área:** qué cambia`, de la más nueva a la más vieja. Mientras no sale, la fecha es «en preparación».
  - Va empaquetado en Roost.Core (`Updates/Changelog`). Lo enseña la pantalla **Novedades** (debajo de Ajustes, `NewsViewModel`), y de ahí salen las notas de la release.
  - `ChangelogTests.LaVersionQueSeCompila_TieneSuEntradaEnElChangelog` **falla** si la `<Version>` de `Directory.Build.props` no tiene su sección. Al subir la versión, primero se escribe su entrada.
- Publicar = subir `<Version>` (con su entrada en `CHANGELOG.md`), etiquetar `v<versión>` y subir la etiqueta. `.github/workflows/release.yml` comprueba que coinciden, pasa los tests, publica con `build/publish-windows.ps1` y `build/publish-linux.sh`, prueba lo publicado y crea la release con `SHA256SUMS.txt` y attestations. Lanzado a mano (`workflow_dispatch`) no publica: deja el artefacto `release-preview`. El README («Publicar una versión») explica la firma y sus secretos.
- `publish-windows.ps1` publica la CLI, el helper y la app autocontenidos y los junta en una carpeta con un solo runtime. Falla si un mismo fichero sale distinto en dos publicaciones (dos exe con versiones distintas de una dll): iguala las dependencias, no lo silencies. El shim va con NativeAOT y **sin** `roost-shim.dll` ni su `deps.json` al lado: si estuvieran, `ShimInstaller` los copiaría a los shims. Si añades un exe o una dll de Roost, añádelo a la lista `$own` del script (lo que se firma) y a `.github/signpath/artifact-configuration.xml`.
- **El instalador de Windows** es `build/roost.iss` (Inno Setup 6, con BOM para los acentos): con lo que publica `publish-windows.ps1`, instala para toda la máquina en `Program Files` (el helper elevado no puede vivir en una carpeta que escriba el usuario), con acceso directo y, si se marca, la carpeta en el PATH del sistema; al actualizar y al desinstalar ejecuta `roost down`, y nunca toca los datos. Su `AppId` es fijo para siempre (si cambia, una versión nueva se instala aparte en vez de actualizar). El workflow lo compila y lo prueba en el runner: instala en silencio, `roost --version`, PATH, desinstala y comprueba que lo quita. En local se compila con `ISCC /DAppVersion=… /DSourceDir=<carpeta publicada> /DOutputDir=… build\roost.iss`, pero **no lo ejecutes en la máquina del usuario sin que lo pida**: instala de verdad. Con el instalador, el aviso de versión lo detecta por el `unins000.exe` que deja junto a los exe (`InstallChannel.Installer`) y propone el instalador nuevo.
- En Linux la release es solo la CLI, single-file. `publish-linux.sh` falla si la publicación deja a su lado algo que `roost` pudiera necesitar.
- NuGet: solo `Roost.Core`, `Roost.Daemon` y `Roost.Extensibility` ponen `IsPackable` a true (los demás heredan false de `Directory.Build.props`). Un proyecto nuevo al que referencien tiene que ser empaquetable también, o el paquete dependería de uno que no existe. `.github/workflows/nuget.yml` (solo manual) los publica.
- **No subas etiquetas, ni crees releases, ni publiques en NuGet sin que lo pida el usuario.** Para probar, ejecuta los scripts en local y `dotnet pack Roost.slnx -c Release -o artifacts/nuget`; `artifacts/` está en el `.gitignore`.

## Arquitectura

Ocho proyectos: `Roost.Cli` → `Roost.Daemon` → `Roost.Extensibility` → `Roost.Core` (la CLI también usa Core directamente), `Roost.Helper`, `Roost.Shim`, `Roost.Catalog` (el generador del feed, que no se distribuye) y `Roost.Desktop` (la app, que usa Core y Daemon y referencia la CLI, el helper y el shim para llevar `roost.exe`, `roost-helper.exe` y `roost-shim.exe` a su lado). `roostd` de ROOST.md es `roost daemon`, un subcomando oculto del mismo `roost.exe`.

### Roost.Core: detección, instalación, configuración y stack
- Hay un `IComponentScanner` por tipo de componente. `ScannerCatalog.CreateDefault()` registra los mismos tres (PHP, MySQL, MariaDB) en todos los SO; cada uno elige internamente las rutas candidatas de Windows o de POSIX. `EnvironmentScanner.ScanAll()` los ejecuta y quita duplicados por ruta completa entre scanners.
- `ComponentScannerBase` es la plantilla. La subclase aporta `EnumerateCandidates()` (cada candidato es una ruta más una etiqueta `Source`, como "Laragon" o "PATH"), `VersionArguments`, `PathExecutableNames` y `Build()`. La base comprueba que el fichero existe, quita duplicados, ejecuta el binario mediante `IProcessRunner` con un timeout de 15 s y marca `IsOnPath` en el ejecutable que resuelve el PATH. `DatabaseScannerBase` contiene el parseo que comparten MySQL y MariaDB.
- **En Linux y macOS, el origen sale de la ruta** (`PosixOrigin`): solo es `system` (ámbar honesto) lo que pone la distro (`/usr/bin`, `/usr/sbin`, `/bin`, `/opt/remi`); `/usr/local` y `/opt` son compilados a mano, tarballs oficiales o imágenes Docker `php:*`, y se evalúan con certeza. Los scanners de MySQL y MariaDB solo prueban servidores (`mysqld`, `mariadbd`), y `ComponentParser.IsClient` descarta además cualquier versión que dé un cliente (`mysql  Ver…`, `mariadb from … client`).
- **Duplicados por la ruta real** (`CanonicalPath`: resuelve los enlaces de todos los componentes; con usrmerge, `/bin/php` es `/usr/bin/php`). `ComponentScannerBase.RealPath` es virtual para probarlo sin enlaces.
- **El orden de los candidatos importa.** Las raíces conocidas (Laragon, XAMPP…) van antes que las entradas del PATH, para que cada binario conserve su `Source` real. El PATH solo añade los binarios que no se habían encontrado ya.
- `ComponentParser` (puro, estático) convierte la salida de `php -v` / `mysqld --version` en un `DetectedComponent`. MySQL y MariaDB se distinguen por el banner "MariaDB": el scanner de MariaDB también prueba binarios `mysqld`, y el parser rechaza el binario cuyo banner no coincide con el tipo esperado, así que ninguno se cuenta como los dos.
- `ComponentVersion` compara por número (8.3.9 < 8.3.36) y guarda el texto original en `Raw`. La auditoría lee `Raw` para detectar sufijos de backport de distro.
- `ComponentKind` ya incluye Nginx y Apache, pero todavía no hay scanners para ellos. Si añades un scanner, actualiza también `ScannerPlumbingTests.CatalogoPorDefecto_RegistraLosTresScannersEnEsteSo`, que exige exactamente tres.
- `RoostPaths` (`Platform/`) es la única fuente de rutas (ROOST.md §8): raíz `C:\roost` con `bin/`, `etc/`, `logs/` y `run/`; caché en `%LOCALAPPDATA%\Roost\cache`; `roost.toml` en `%APPDATA%\Roost`; www **siempre** en `<raíz>/www` (`C:\roost\www`, nunca `C:\www`). Con `ROOST_HOME` todo va bajo esa carpeta.
- `RoostException` (namespace raíz) es el error para el usuario de cualquier operación (instalar, configuración, stack). Lleva un mensaje para enseñarlo tal cual, y quien la lanza no deja nada a medias.
- **Instalación** (`Install/`, sigue ROOST.md §5.4). `InstallFiles` tiene las reglas comunes:
  - descarga a un `.part-…` y verifica el SHA-256 **antes** de extraer, con caché que se reutiliza si el hash cuadra;
  - `ExtractAtomic` extrae a una carpeta `.tmp-…` y la renombra al final (con `stripSingleRoot` para zips que lo meten todo en una carpeta);
  - `RemoveAtomic` primero renombra a `.trash-…`, así que si hay un fichero en uso falla sin tocar nada;
  - las carpetas con punto inicial son de trabajo: las ignoran los listados y el scanner, y `CleanLeftovers` las limpia.
  - `WindowsPhpCatalog`:
    - tiene un `Parse` puro del `releases.json` de windows.php.net, que solo trae la última de cada rama con el SHA-256 de cada variante;
    - `Resolve` convierte "8.3" o "8.3.36" en un `PhpBuild`;
    - `FetchAsync` resuelve los zips contra la URL final, después de las redirecciones.
  - `PhpInstaller` instala en `bin/php/<versión>-<nts|ts>-<arq>`. `Match` busca instalaciones por nombre, versión o rama, y lo usan `remove`, `use` y el planificador.
  - `NginxCatalog` fija **a mano** nginx 1.30.6 con su SHA-256, porque nginx.org solo publica firmas PGP. Para cambiar de versión: descarga el zip y su `.asc`, verifica la firma con `gpg` contra las claves de nginx.org/keys y fija el SHA-256 del zip verificado. `NginxInstaller` instala en `bin/nginx/<versión>`.
  - `ApacheCatalog` fija **a mano** Apache 2.4.69 de Apache Lounge (Win64, VS18), porque Apache no publica binarios para Windows. Para cambiar de versión: el zip y su `.asc` de apachelounge.com/download, `gpg --verify` con la clave de Steffen Land (huella `3D49885EADE8BC399F46D5DDBEE88A7829C17558`) y el SHA-256 del zip verificado. `ApacheInstaller` instala en `bin/apache/<versión>` **solo la carpeta `Apache24/`** del zip (`ExtractAtomic(..., fromFolder:)`), sin el ReadMe de al lado. Necesita el VC++ 2017-2026 14.50+ (`ApacheBuild.Redistributable`), que Roost no instala.
  - **Bases de datos** (`Database.cs`, `DbCatalog.cs`, `DbInstaller.cs`):
    - `DbEngine` (MariaDb, MySql) y las extensiones de `DbEngines`: clave, ejecutables (`mariadbd`/`mysqld`, `mariadb-admin`/`mysqladmin`, `mariadb`/`mysql`).
    - `DbSpec` parsea `motor@version`: la versión puede ser mayor (`8`), rama (`11.4`) o exacta. `DbCatalog.Resolve` sin versión da la LTS más nueva del motor, y una versión exacta tiene que ser la última de su rama.
    - `MariaDbCatalog` lee la API REST de downloads.mariadb.org (ramas `Stable` y `/<rama>/latest/`), con `ParseMajors` y `ParseLatest` puros. La API da las URLs en `http://`: se suben a https y se añade archive.mariadb.org de respaldo.
    - `MySqlCatalog` fija **a mano** 8.4.11, 8.0.46 y 5.7.44, porque Oracle no tiene API y las páginas de dev.mysql.com bloquean a los clientes que no son navegadores. El CDN sí sirve el zip, su `.asc` y su `.md5`. Para actualizar: el zip y su `.asc` de cdn.mysql.com, `gpg --verify` con `RPM-GPG-KEY-mysql-2023` (o `-2022` para la 5.7) de repo.mysql.com, y el SHA-256 del zip verificado. Cada build lleva dos URLs (`Downloads/` y `archives/`), porque la release pasa a `archives/` cuando sale la siguiente; `DownloadVerifiedAsync` prueba la siguiente si una da 404.
    - `DbInstaller` instala en `bin/<motor>/<versión>`, quitando la carpeta envolvente del zip. `Remove` solo quita binarios: **los datos de `data/` no los borra nada en Roost**.
- **Configuración** (`Config/RoostConfig`): `roost.toml` con `[php] version/pool`, `[web] port/https_port/server` (`server`: `nginx`, por defecto, o `apache`; `SetWebServer`) y `[db] engine/version/port` (`engine = "none"` para no arrancar ninguna) y `[git] exe` (otro git en lugar del MinGit de Roost; `SetGitExe`). Es opcional; si no existe, valen los valores por defecto. Se lee y se edita con el **árbol de sintaxis de Tomlyn** (no con su serializador), así `SetPhpVersion` y `SetDb` cambian sus claves y conservan comentarios y el resto. Si el fichero no existe, escriben la plantilla con los valores ya puestos.
- **Supervisor** (`Services/`): `Supervisor` sobre `IProcessLauncher` (el real es `OsProcessLauncher`, con stdout/stderr añadidos a `LogFile`).
  - El primer arranque es síncrono y en orden, y `StopAsync` para en orden inverso.
  - Una salida tras vivir `healthyUptime` (10 s) es reciclaje (php-cgi sale tras `PHP_FCGI_MAX_REQUESTS`) y se relanza en el acto. Una salida antes es una caída: se relanza con espera creciente y a las `MaxQuickCrashes` (5) seguidas queda `Failed`.
  - `Slot.Stop` mata **antes** de cancelar, y bajo lock: el orden importa (ver el comentario).
  - **Parada limpia:** un servicio con `StopCommand` (las bases de datos: `admin shutdown`) se para lanzando ese comando y esperando a que su bucle termine (`StopTimeout`, 30 s); si no, se mata. `BeginGracefulStop` marca el slot como «parando», y desde ahí su salida no es una caída ni un reciclaje.
- **Stack** (`Stack/`): `StackPlanner.Prepare` genera en `etc/` el `php.ini` de la instalación (`PhpIni`, puro), crea `conf.d/99-usuario.ini` si no existe y genera `nginx.conf` (`NginxConf`, puro, escucha solo en 127.0.0.1). Después:
  - crea www con una portada solo si no existía;
  - valida con `nginx -t`, buscando "test is successful" en la salida;
  - devuelve los `ServiceDefinition`: un php-cgi por puerto (`PhpPool.Ports`: `9000 + mayor·100 + menor·10 + i`) y nginx.

  Con base de datos, `PrepareDb` va antes de todo eso:
  - genera `etc/<motor>/<rama>/my.ini` (`DbIni`, puro: 127.0.0.1, utf8mb4, `[client]` con root, y `mysqlx = OFF` solo en MySQL ≥ 8, porque la 5.7 no arrancaría con esa opción);
  - `EnsureDataDir` crea `data/<motor>/<rama>` la primera vez, en una carpeta `.tmp-…` que renombra al final (`mariadb-install-db --datadir`, o `mysqld --defaults-file=… --initialize-insecure --datadir`), y comprueba que exista `mysql/`. Un datadir no vacío sin `mysql/` no se toca;
  - el servicio de la base de datos va el **primero** (DB → PHP → nginx), con `--console` y `StopCommand`.

  Con sitios hay además:
  - **un pool de php-cgi por rama en uso**: la del PHP global primero, y después la de cada sitio que fije otra (`ResolveSitePhp`). Dos sitios de la misma rama comparten pool, y el `upstream` por defecto es el del global;
  - un `etc/nginx/sites/<sitio>.conf` por sitio (`SiteConf`, puro). La primera vez se escribe entero (`RenderNew`); después solo se sustituye el bloque entre `# roost:begin/end` (`Merge`). Sin marcas no se adivina: se lanza `RoostException`;
  - el certificado de cada sitio (`LocalCa.EnsureSiteCert`);
  - `nginx.conf` termina con `include sites/*.conf`, y `StackPlan.Ports` incluye el puerto HTTPS si hay sitios.
  - `PrepareNginx` y `PrepareApache` apartan antes como `.conf.removed` los `.conf` de su carpeta cuyo sitio ya no está registrado (los que dejó una versión anterior al quitar un sitio con el otro servidor activo).

  **Con Apache** (`[web] server = "apache"`), `Prepare` lanza `RoostException` si no está instalado (`up` lo instala antes) y cambia solo el servidor: los pools de php-cgi son los mismos.
  - `ApacheConf` (puro) genera `etc/apache/httpd.conf`: `Listen 127.0.0.1:<puerto>`, los módulos de `ApacheConf.Modules`, un `<Proxy balancer://php-X.Y>` por pool y el VirtualHost de localhost. Todo va con `AllowOverride All`, así que lee los `.htaccess`. PHP va por **mod_proxy_fcgi**, no mod_php, así que sirve el mismo PHP NTS y cada sitio con el suyo.
  - **Dos trampas de Windows** (probadas en real; los tests solo comprueban el texto):
    - el handler es `proxy:balancer://php-8.4/` **con la barra final** (`ApacheConf.PhpHandler`): Apache pega detrás la ruta del fichero (`C:/...`), y sin la barra el balancer se llamaría `php-8.4C:`;
    - `ProxyFCGISetEnvIf "true" SCRIPT_FILENAME "%{DOCUMENT_ROOT}%{reqenv:SCRIPT_NAME}"`, como el `$document_root$fastcgi_script_name` de nginx: la ruta que arma mod_proxy_fcgi sale como `/C:/...` y con `%20` en los espacios.
  - `ApacheSiteConf` es el `etc/apache/sites/<sitio>.conf`, con dos VirtualHost (HTTP y HTTPS) entre las mismas marcas que `SiteConf` y el mismo `RenderNew`/`Merge`.
  - Se valida con `httpd -t -f <conf>` (`TestApache`, busca "Syntax OK"). El servicio se llama `apache` y se para matando su árbol, así que `Prepare` borra el `run/apache.pid` viejo; si no, el log avisaría de un «Unclean shutdown».

  - `DbIni.Write` genera `etc/<motor>/<rama>/my.ini` con la contraseña de root que guarda `DbPasswords` en `[client]`. `DbPasswords` vive en `state/db-passwords.json`, por instancia `motor-rama`, con JSON source-gen. El `my.ini` acaba con `!include …/usuario.cnf`, que se crea la primera vez con ejemplos y en el que manda el usuario.
  - `DbAdmin` hace todo lo que va dentro del servidor en marcha:
    - crear, listar y borrar bases de datos (`Drop`, nunca las `SystemSchemas`);
    - la contraseña de root (`ChangeRootPassword`): `ALTER USER` en todas las cuentas root de `mysql.user`, guardar, reescribir el `my.ini` y comprobar con un `SELECT`. Si root no entra, lo deja como estaba; `saveOnly` es para cuando se cambió por fuera;
    - los usuarios: `ListUsers` (con su acceso por `mysql.db`), `CreateUser` como `'u'@'%'`, `SetUserPassword` y `DropUser`.
  - Todo lo que entra en el SQL pasa por un patrón cerrado (`IsValidName`, `IsValidUserName`, `IsValidHost`, `IsValidPassword`: ASCII imprimible sin espacio, `'`, `"`, `\` ni `` ` ``), así que nunca hace falta escapar nada.
  - `SmokeTest` dice qué Visual C++ Redistributable falta si `--version` no contesta; lo usan la CLI y la app.
  - En la CLI, `roost db` añade `drop`, `password` y la rama `user` (`DbAdminCommands.cs`). `DbSession.ReadPassword` pide sin eco o, si no hay terminal, lee una línea de stdin (tolera el BOM de PowerShell); `Confirm` exige `--yes` sin terminal.
  - En la app, el puerto de la BD se edita **solo** en la tarjeta de conexión de Bases de datos (Ajustes remite). Están los diálogos de contraseña y de usuario, y `Services/DbClients` detecta HeidiSQL, TablePlus y DBeaver (rutas y registro, solo lectura) y los abre con la conexión.
  `ResolvePhp` elige el PHP global, prefiriendo NTS. `WritePhpIni` genera el php.ini de una instalación con las extensiones que ha elegido el usuario (`PhpExtensionChoice`); lo usan `Prepare` y la app. `ResolveDb` elige la base de datos global: null con `none` o si no hay ninguna; sin `[db]`, la primera de `ListInstalled` (MariaDB más nueva). `BusyPorts` y `AcceptsConnectionsAsync` son las sondas de puertos.
- **Sitios** (`Sites/`):
  - `Site` (nombre = etiqueta DNS, carpeta, `Root` relativo, `Php` opcional) y `SiteRegistry` (`state/sites.json`, escritura atómica; `Upsert` hace idempotente el `site add`).
  - `SiteManager`:
    - da de alta (`Add`): crea la carpeta con un index.php si no existe (en relativo, cuelga de la www), usa `public/` si la hay y la carpeta es nueva o cambia, valida el PHP, y valida `--root` antes de crear nada;
    - cambia la raíz web (`SetRoot` / `NormalizeRoot`): null, vacío o `.` es la carpeta; lo demás se guarda relativo con `/`, y tiene que existir y quedar dentro del proyecto, también resolviendo enlaces;
    - da de baja (`Remove`): aparta los `.conf` de nginx **y** de Apache, borra el certificado y nunca toca la carpeta;
    - sincroniza el hosts. `ConfFile` da el vhost del servidor activo; `TypicalRoots` son las subcarpetas que la app ofrece como accesos rápidos.
    - Lo usan `roost site add/remove/root` y la app.
  - `HostsBlock` es el bloque `# roost:begin/end` del `hosts`: puro, `internal`, y **también se compila dentro de roost-helper** (enlazado como fuente), así que no puede depender de nada más de Roost. Solo admite `<etiqueta>.test`, y un bloque abierto sin cierre no se toca.
  - `HostsUpdater` lee el `hosts` (sin privilegios) y, si el bloque cambia, lanza `roost-helper hosts-sync <fichero>` con `Verb = "runas"` (UAC; cancelar es el error 1223). Con `ROOST_HOSTS_FILE` escribe directamente un `hosts` de pruebas.
  - El `hosts` se lee y se escribe en **Latin-1**, para conservar byte a byte lo que no es de Roost (puede haber comentarios en ANSI).
  - `LocalCa` hace la CA y los certificados en C# puro (`CertificateRequest`): raíz ECDSA P-256 de 10 años; sitio con SAN `<n>.test` y `*.<n>.test`, EKU serverAuth y 397 días. `EnsureSiteCert` reemite el certificado si falta, si caduca en menos de 30 días o si no encadena con la CA actual. Las claves privadas llevan una ACL protegida, solo para el usuario actual. `Trust` y `Untrust` usan `CurrentUser\Root` (sin admin, con diálogo de Windows); `Describe` da el nombre y la huella para enseñarlos.
- **git** (`Git/`, el diseño de «git al estilo Plesk» de `roost-source/planes`):
  - `ICommandRunner`/`CommandRunner` (en `Scanning/`, junto a `IProcessRunner`, que sigue sin lanzar nunca): órdenes con `ArgumentList`, código de salida, entorno (un null quita la variable heredada), cwd, líneas en vivo (también partidas en `\r`, el progreso de git) y stdin. Mata el árbol de procesos al cancelar o al pasarse del tiempo; el timeout da `TimedOut` sin lanzar y la cancelación lanza. Lo usa git y lo usarán Composer, Redis y Node. Tests con `cmd /c`; para lo demás, `FakeCommandRunner` (tests), guionizado por prefijo de argumentos.
  - `GitCatalog` fija **a mano** MinGit 2.55.0.3 x64 con el SHA-256 del `digest` de la API de GitHub (release `v2.55.0.windows.3`), comprobado con el zip descargado. Para cambiar de versión: el asset `MinGit-<v>.<build>-64-bit.zip` (no el busybox) y su digest, bajarlo y comprobarlo. `GitInstaller` instala en `bin/git/<versión>` exigiendo `cmd/git.exe` y `git-remote-https.exe`. `GitTool.Locate` prefiere `[git] exe`; `EnsureAsync` instala la primera vez que hace falta.
  - `GitEnvironment.Build`: `GIT_CONFIG_NOSYSTEM`, `GIT_CONFIG_GLOBAL=state/git/gitconfig` (regenerado: longpaths, `autocrlf=false`, Schannel, `useHttpPath`, `pull.ff=only`), sin prompts, `LC_ALL=C` para clasificar los mensajes, y quita `GIT_ASKPASS`, `GIT_DIR` y compañía heredados. Las credenciales entrarán por `extraConfig` (`GIT_CONFIG_COUNT`) y la clave por `sshCommand`; sin ella, `ssh -o BatchMode=yes` (nunca se queda esperando una pregunta).
  - `GitUrl` (puro): https, ssh, `git@host:ruta` y carpetas locales; rechaza `usuario:contraseña@` y `git://`; da el proveedor, la etiqueta (`acme/tienda`) y el nombre de sitio sugerido. `GitClient` sobre el runner: probe (`ls-remote --symref`), clone con `--progress` en porcentaje y `core.autocrlf=false` en el `.git/config` del clon, status v2, log con `%x1f`, fetch, pull **solo `--ff-only` y solo limpio**, checkout solo limpio. Siempre con `--` antes de la URL, y las ramas validadas. `GitOutput` son los parsers (salidas reales grabadas en los tests) y `GitFailure.Classify` convierte el stderr en los textos del diseño. `GitException` deriva de `RoostException` (que por eso ya no es `sealed`).
  - `Site.Git` (`SiteGit`: remoto normalizado, rama, tipo de acceso, proveedor, último commit; **nada secreto**) es nullable y va en `SiteJson` (el shim es NativeAOT). `SiteManager(paths, git)` (`SiteManager.Git.cs`): `CloneAsync` clona en `www/.tmp-<guid>` y renombra con `MoveDirectory` (si falla o se cancela, no queda nada; en la www solo se limpian esos `.tmp-<guid>`), `PullAsync`, `CheckoutAsync`, `GitStatusAsync`, `AdoptGitAsync`. Historial en `state/git/<sitio>/history.json` (`GitHistory`) y salida completa en `logs/git-<sitio>.log` (`GitLog`, que tapa cualquier `://usuario@`). `Add` conserva `Git` sobre la misma carpeta.
  - Tests sin red ni git real; integración opcional con `ROOST_TEST_GIT=1` (git del PATH) o la ruta de un `git.exe`: `git init --bare`, clonar por ruta, pull ff, cambios locales, checkout y divergencia.
- **DNS local** (`Dns/`, ROOST.md §5.7):
  - `DnsMessage` (puro) lee una pregunta y la contesta. Rechaza las respuestas, los opcodes que no son QUERY, más de una pregunta, etiquetas de más de 63 bytes, nombres de más de 253 y lo truncado. `A` de `*.test` da 127.0.0.1 (TTL 60); `AAAA` va vacío (NODATA: nginx solo escucha en IPv4, y así el navegador no prueba `::1`); cualquier otro nombre, REFUSED; lo ilegible, FORMERR.
  - `LocalDnsServer` escucha UDP en `127.53.0.1:53` (`LocalDns.EndPoint`), con la dirección en uso exclusivo en Windows. Si está cogida, lanza `RoostException`. Una dirección de loopback propia no choca con otro DNS local ni con la Conexión compartida a Internet de Windows, que suele tener `0.0.0.0:53`.
  - `LocalDns` lee las reglas NRPT del registro (`HKLM\...\Dnscache\Parameters\DnsPolicyConfig`, sin privilegios), reconoce la de Roost por su comentario «Roost» (`HasRoostRule` y `ForeignRule`, puros) y la pone o la quita con `roost-helper dns-enable/dns-disable` (UAC). Nunca pisa una regla de `.test` de otra herramienta.
  - `NrptScripts` son los dos scripts de PowerShell fijos que ejecuta el helper: `internal` y **compilado también dentro de roost-helper**, como `HostsBlock`.
  - `LocalDnsProbe` pregunta al servidor directamente (1 s; nunca lanza). Lo usa `roost dns status`.
  - `RoostConfig.SitesResolver` es `[sites] resolver = "hosts" | "dns"` (por defecto, hosts), y `SetResolver` lo cambia. Con `dns`, `SiteManager.SyncHosts` no toca el hosts.
- `PhpScanner` busca primero en la raíz de Roost (origen `"Roost"`), así que lo instalado aparece en `scan` y `audit`.
- **Actualizaciones de Roost** (`Updates/`): solo avisan; nunca descargan ni sustituyen nada.
  - `RoostVersion.Current` es la versión que corre: el `AssemblyInformationalVersion` (de `Directory.Build.props`) sin lo que va tras `+`. `SemanticVersion` compara como SemVer 2.0 (`0.2.0-beta.1` < `0.2.0`, `beta.2` < `beta.11`, la metadata `+…` no cuenta); no es `ComponentVersion`, que es para PHP y las bases de datos.
  - `GitHubReleases` (puro) parsea `/releases/latest` (y también la lista de `/releases`): descarta borradores, prereleases y etiquetas con guion, y exige `html_url` en https. `TryParse` distingue «no hay ninguna» (true con null) de «no se entiende» (false con el motivo). `Summarize` deja las notas en unas pocas líneas sin Markdown.
  - `UpdateChecker.CheckAsync` **nunca lanza**: devuelve un `UpdateCheck` con `UpToDate`, `Available`, `NoReleases` (la API da 404) o `Failed` (con `Error`, sin tildes). Pide con `User-Agent: roost/<versión>`, `Accept: application/vnd.github+json` y un timeout propio de 10 s.
  - La respuesta buena (también el 404) se guarda en `<caché>/update-check.json` y vale 24 h (`force` la salta). Guarda la release con los nombres de campo de GitHub, que se leen con el mismo parser, y la compara al leerla con la versión de ahora (actualizar Roost no deja un aviso viejo). Un fallo no la pisa: el resultado `Failed` trae la última `Latest` conocida, y `UpdateAvailable` sigue avisando sin red. Una caché de otra URL o del futuro no vale.
  - `UpdateChecker.AutomaticChecksOff` es `DisabledByEnvironment` más CI (`CI` o `GITHUB_ACTIONS`): lo miran el daemon (`Roost.Daemon/UpdateRefresher`: comprueba al minuto de arrancar y cada 6 h, sin bloquear `up`, y apunta en `roostd.log` lo que encuentra) y la línea de aviso de `roost status` y `roost up` (`UpdateNotice`, solo con la caché). Las dos versiones puras (`DisabledIn`, `AutomaticChecksOffIn`) tienen tests. Para probar el daemon: `ROOST_UPDATE_URL` a un servidor local con una release más nueva, `roost up`, esperar un minuto y mirar `roost status`.
  - `ROOST_NO_UPDATE_CHECK=1` (`DisabledByEnvironment`) apaga solo lo automático: lo comprueba quien llama (la app y la línea de `roost status`), y `roost update` y el botón de Ajustes siguen funcionando. `ROOST_UPDATE_URL` cambia la URL de la API.
  - `UpdateInstructions.Detect` (puro, sobre la ruta) saca el canal: `…\scoop\apps\roost\…` (o `…\apps\roost\current\` con un `SCOOP` propio), `…\WinGet\Packages\Roost.Roost_…` y `…\WinGet\Links\`, `bin\Debug|Release` (compilada) o manual. `For` da el comando (`scoop update roost`, `winget upgrade Roost.Roost`) o, en manual, el paquete de la release para este SO.

### Roost.Extensibility: los puntos de enganche (ROOST.md §12.4)
- El contrato (`Contracts.cs`): una extensión implementa `IRoostExtension` (id, nombre, versión, `ApiVersion`) y, según lo que aporte, `IStackExtension` (servicios que arrancan tras la base de datos y antes de PHP), `IAuditExtension` (un dataset que se mezcla con el de Roost con `AuditDataset.Merge`, y scanners) o `ICliExtension` (comandos de Spectre). Las pantallas son `IDesktopExtension`, en `Roost.Desktop/Extensibility`, porque dependen de Avalonia.
- `ExtensionLoader` carga `bin/extensions/<Nombre>/<Nombre>.dll`, cada una en su `AssemblyLoadContext` pero **compartiendo lo que ya tiene Roost** (si no, los tipos del contrato no casarían). Nunca lanza: lo que no carga (dll rota, sin clase que implemente `IRoostExtension`, id inválido o repetido, `ApiVersion` más nueva que `ExtensionApi.Current`) va a `Errors`.
- `ExtensionSet` es la única puerta por la que Roost llama a una extensión: lo que lanza se apunta como error y se sigue sin ella (`Guard`). La excepción es el stack: si una extensión no prepara sus servicios, `up` falla con su nombre. Un servicio de extensión no puede repetir el nombre de uno de Roost (`Prepare` lo rechaza).
- La CLI las carga una vez al arrancar (`CliExtensions`) y se las pasa al daemon (`DaemonHost.RunAsync`); la app, en `AppServices.Extensions`. Sube `ExtensionApi.Current` solo si cambias algo del contrato que rompa a las extensiones ya compiladas.

### Roost.Shim: el php de la terminal (roost-shim.exe)
- `roost shims install` lo copia como `bin/shims/php.exe` (`ShimInstaller`, en Core: con NativeAOT es un solo fichero; en una compilación normal se copian también su dll y las de su `deps.json`) y pone esa carpeta la primera en el PATH del **usuario** (`UserPath`: `HKCU\Environment`, sin expandir y conservando `REG_EXPAND_SZ`, y avisa con `WM_SETTINGCHANGE`).
- En cada llamada, `TerminalPhp.Resolve` (Core, puro sobre disco) elige el PHP: `ROOST_PHP_VERSION`, el `.php-version` más cercano hacia arriba (acepta `php 8.3`), el sitio cuya carpeta contiene a la actual (el más profundo) o el global. Lo que se pide y no está instalado es un error, no otro PHP.
- Ejecuta el `php.exe` real con la línea de órdenes tal cual (`ArgumentsAfterProgram`: no se reentrecomilla) y con `PHPRC` y `PHP_INI_SCAN_DIR` en el entorno, para que los PHP que lance (Composer) carguen el mismo php.ini; si ese php.ini no existe aún, lo genera. La raíz de Roost la saca de su ubicación (`RoostPaths.ForRoot`).
- Se publica con NativeAOT: lo que usa de Core no puede depender de la serialización por reflexión (`SiteJson` y `PhpExtensionJson` son contextos generados).
- Windows mira el PATH del sistema antes que el del usuario: un `php.exe` en el del sistema gana al shim. `roost shims status` y Ajustes lo avisan.

### Roost.Helper: roost-helper.exe (lo único que corre elevado)
- `hosts-sync <fichero>`, con un nombre por línea. El `hosts` es una ruta fija (`Environment.SystemDirectory`). Valida con `HostsBlock.IsValidHost` y rechaza peticiones de más de 64 KB o de más de 1000 nombres.
- `dns-enable` y `dns-disable`, sin argumentos, ponen o quitan la regla NRPT de `.test` ejecutando un script fijo (`NrptScripts`) con el PowerShell del sistema (ruta fija, `-NoProfile -ExecutionPolicy Bypass -EncodedCommand`, 60 s como mucho). `dns-enable` no pisa una regla de `.test` que no sea de Roost.
- Registra cada ejecución en `%ProgramData%\Roost\roost-helper.log`, también una ruta fija. Lo rechazado no se registra.
- Códigos de salida: 0 bien, 64 uso, 65 petición inválida, 69 ya hay una regla de `.test` que no es de Roost, 70 falló el script, 74 error de E/S (por ejemplo, no está elevado).
- Reglas: sin dependencias, sin rutas de escritura que vengan de fuera, sin variables de entorno. Cualquier operación nueva tiene que ser igual de cerrada (ROOST.md §10, riesgo 4).

### Roost.Daemon: roostd (ASP.NET Core, Kestrel)
- `DaemonHost.RunAsync`:
  - un mutex por usuario: si ya hay un daemon, este sale;
  - `WindowsJob` mete al daemon en un Job Object con KILL_ON_JOB_CLOSE, así que los hijos lo heredan y mueren con él;
  - Kestrel escucha en la named pipe `roost-<usuario>` (solo el usuario actual) o en un unix socket, nunca por TCP;
  - endpoints `GET /status`, `POST /up`, `POST /restart/{servicio}` y `POST /down` (que para el daemon después de responder);
  - log en `logs/roostd.log`.
- `StackService` serializa las operaciones. `up` con el stack en marcha lo para y lo rearranca con la configuración actual (así se aplica `php use`).
  - `up` **no se cancela aunque el cliente se desconecte**, y en un `finally` para cualquier supervisor que no haya quedado como el actual. Si no, un Ctrl+C dejaba procesos sin dueño.
  - Espera de salud: que todos estén `Running` y acepten conexiones (hasta 45 s, por el primer arranque de MySQL 8). Si no, devuelve el motivo con el final de su log. El puerto de la base de datos también se comprueba antes de arrancar.
- `restart` llama a `Supervisor.RestartAsync`: para el grupo (limpio si tiene comando de parada) y el bucle lo relanza en el acto sin contarlo como caída; uno `Failed` se vuelve a lanzar. No regenera la configuración (eso es `up`).
- Con `[sites] resolver = "dns"`, `up` arranca también el DNS local (`LocalDnsServer`, dentro del proceso del daemon y fuera del supervisor) y lo para con el stack. `status` lo enseña como el servicio `dns`, y `restart dns` lo relanza. Si falta la regla NRPT, lo apunta en el log y arranca igual.
- Un `up` que falla por un puerto ocupado nombra al dueño (`PortOwners`, en Core: `GetExtendedTcpTable`, IPv4 e IPv6).
- `DaemonClient` usa `HttpClient` sobre la pipe (`ConnectCallback`, `PipeOptions.CurrentUserOnly`). `TryStatusAsync` devuelve null si el daemon no está; `EnsureRunningAsync` lo lanza con el `ProcessStartInfo` que le pase la CLI.

### Roost.Cli: comandos, auditoría y salida (Spectre.Console.Cli)
- `roost php` es una rama (`AddBranch`) con `list`, `install`, `remove` y `use`. `roost db` (`Commands/Db/DbCommands.cs`) es otra, con `list`, `install`, `remove`, `use` y `create`.
  - `CatalogAudit` pasa una versión suelta (PHP o base de datos) por el `AuditEvaluator` para avisar de lo EOL al listar o instalar.
  - `db create` valida el nombre (`[A-Za-z0-9_]`), ejecuta el cliente con el `my.ini` de la rama y confirma el resultado con un `SELECT 'roost-ok'`, porque `ProcessRunner` no da el código de salida.
- `roost site` y `roost cert` (`Commands/Sites/SiteCommands.cs`):
  - `site add` crea la carpeta si no existe (con un `index.php`), usa `public/` si existe y acepta `--php global` para quitar el PHP propio;
  - `site remove` aparta el `.conf` como `.conf.removed` y borra el certificado, pero **nunca la carpeta del proyecto**;
  - `SiteSupport.SyncHosts` solo pide UAC si el bloque cambia, y con el DNS local no hace nada.
- `roost git` (`Commands/Git/GitCommands.cs`: `install`, `status`; `GitSetup.EnsureAsync` instala MinGit la primera vez) y `roost site clone / pull / branch / git` (`Commands/Sites/SiteGitCommands.cs`). `site clone` comprueba el remoto antes de clonar, pide confirmación si hay terminal y, con `--json`, sincroniza el hosts en silencio y no aplica el stack.
- `roost dns` (`Commands/Sites/DnsCommands.cs`): `install` pone la regla si falta (UAC) y pasa a `dns`; `remove` la quita si está, vuelve a `hosts` y lo rellena con los sitios; `status` dice el modo, la regla y si el DNS contesta. `install` y `remove` rearrancan el stack si está en marcha.

  `StackApply.IfRunningAsync` rearranca el stack si está en marcha, y lo usan `db use`, `site add/remove` y `cert renew`.
  - `RoostHttp.Client` es el único `HttpClient` a internet. Su timeout cubre solo las cabeceras: la descarga del zip no tiene límite.
  - Los comandos que instalan o sirven comprueban `OperatingSystem.IsWindows()`.
- `Commands/Stack/StackCommands.cs` tiene `up` (comprueba la configuración y que haya un PHP, instala el servidor web de `[web] server` si falta, asegura el daemon y llama a `/up`), `down`, `status` y `daemon` (oculto).
  - Los comandos sin opciones usan `EmptyCommandSettings`: con `CommandSettings` Spectre falla, porque es abstracta.
  - `DaemonLauncher` (en `Roost.Daemon`, para que lo use también la app de escritorio) lanza el daemon en Windows con **ShellExecute y la ventana oculta**. Con `UseShellExecute=false` el daemon heredaría el stdout de quien lo lanza, y cualquiera que capture la salida de `roost up` (scripts, CI) se colgaría hasta que muriera el daemon.
- La auditoría vive en Core (`Roost.Core/Audit`, como pide ROOST.md §5.1), porque la usan la CLI y la app de escritorio:
  - `AuditDataset` es el modelo JSON. Se carga del recurso embebido `Audit/advisories.json` de Roost.Core, o del fichero que se pase con `--dataset`.
  - `VersionRange` evalúa rangos formados por restricciones separadas por espacios que deben cumplirse todas (AND), como `>=8.3.0 <8.3.36`. Un aviso aplica si casa *alguno* de sus rangos `affected`.
  - `AuditEvaluator` es puro, y se le puede inyectar `today` (la fecha de referencia para el EOL).
- Orden del veredicto en `AuditEvaluator.Evaluate`:
  1. Una rama que no está en el dataset da Unknown (ámbar).
  2. EOL (estado `"eol"`, o fecha EOL igual o anterior a hoy) tiene prioridad sobre Vulnerable y da rojo.
  3. Un aviso que casa da Vulnerable (rojo).
  4. Si no, el veredicto es Patched si el producto tiene avisos en el dataset, o Supported si no tiene ninguno (hoy, MySQL y MariaDB); los dos en verde.
- Después se aplica el **«ámbar honesto»**. Si el `Source` es un gestor de paquetes (`system`, `apt`, `dnf`…) o la versión cruda trae un sufijo de distro (`+deb`, `+esm`, `ubuntu`, `.el9`…), el hallazgo pasa a `Uncertain` y ámbar, y no hace fallar la CI. El motivo (ROOST.md §7.5): un falso rojo quema más confianza que un ámbar honesto. Mantén este comportamiento.
- El titular de un vulnerable nombra como mucho tres avisos (KEV primero, luego por CVSS o gravedad) y «y N mas»: con el feed entero, un PHP viejo casa con decenas. Sin CVSS, dice la gravedad («gravedad critica»).
- `AuditFinding.IsBlocking` decide el código de salida. Las mitigaciones se copian de los avisos, pero todavía no se comprueban contra el `php.ini` efectivo.
- El código de `Rendering/` saca tablas de Spectre o JSON (opciones compartidas en `JsonOutput.Options`).
- El dataset empaquetado es `src/Roost.Core/Audit/advisories.json` y se embebe al compilar; es el respaldo sin red. `AuditEvaluatorTests` comprueba su contenido real (por ejemplo, que 8.3.36 es la última de la 8.3), así que tocar los datos de ramas puede romper tests.
- **El feed firmado** (`roost-catalog`, ROOST.md §7.3):
  - **Cliente, en Core (`Audit/AdvisoryFeed`):** baja `advisories.json` y su `.sig` de la rama `catalog` (raw.githubusercontent.com), con ETag y una caché de 24 h en `cache/catalog/`. Solo guarda lo que firma una clave de `FeedSignature.TrustedKeys` (ECDSA P-256 sobre SHA-256, en DER). Rechaza un feed más viejo que el guardado y uno con un `schemaVersion` mayor que `SupportedSchemaMajor`, y no lanza nunca. `LoadBest` elige entre la caché y el empaquetado el más nuevo y devuelve un `DatasetOrigin`, que se enseña en la cabecera, en el JSON y en la app.
  - **Generador, `src/Roost.Catalog` (`roost-catalog`):**
    - `Sources` tiene los lectores puros de cada fuente. Los rangos de GHSA se acotan a su rama (`< 8.3.36` → `>=8.3.0 <8.3.36`), y lo que no se lee sin adivinar se descarta.
    - `CatalogBuilder` lo junta con `catalog/` y aplica las reglas: retiene lo que dejaría en rojo la última versión de una rama y no publica si se pierden datos de golpe.
    - Lo lanza `.github/workflows/catalog.yml` cada día y lo publica en la rama `catalog`, con `* -text` para que git no cambie los bytes firmados.
  - **Lo escrito a mano** va en `catalog/advisories/<id>.json` y se aplica por id, nunca por alias (dos avisos comparten «PHP2Shell»). Ver `catalog/README.md`.
  - **Tests sin red:** `tests/Roost.Catalog.Tests` usa trozos del JSON real de cada fuente, y `AdvisoryFeedTests` usa un servidor falso.

### Roost.Desktop: la app (Avalonia 12, CommunityToolkit.Mvvm)
- Sin lógica de negocio propia: llama a Core (instalar, configurar, auditar) y al daemon como la CLI. `DaemonLauncher.FromDirectory` lanza el `roost.exe` que va junto a `roost-desktop.exe`.
- `Theme/`: `Tokens.axaml` (los colores del mockup, en claro y oscuro: siempre `DynamicResource`), `Icons.axaml` (geometrías de 24×24 para `Controls/Icon`) y `Styles.axaml` (el botón tiene tema propio y se combina por clases: `primary`, `ghost`, `sm`, `icon`, `nav`, `tile`, `vcard`, `row`…). `Chip`, `Dot` y `Note` llevan un `Tone` (el semáforo).
- `Services/`: `AppServices` (rutas, `HttpClient`, `DaemonClient`, preferencias en `state/desktop.json`), `StackController` (estado del daemon; arrancar, parar y aplicar como `roost up`; diagnostica los puertos ocupados), `AuditState` (escaneo en segundo plano: contador rojo, bandeja, Auditoría), `Shell` (navegador, explorador, terminal con el PHP global en el PATH) y `SingleInstance`.
- `MainViewModel` lleva la navegación (una `PageViewModel` por pantalla, que se conserva), sondea el daemon cada 3 s y avisa a las pantallas con `StackChanged` solo si cambia algo. `NotifyConfigChanged` fuerza el aviso tras tocar disco. Los diálogos son `DialogViewModel` sobre la ventana (`ShowDialogAsync` espera al resultado); `RunAsync` enseña en un diálogo los errores para el usuario.
- La barra de título es propia (`WindowDecorations="BorderOnly"` + `ExtendClientAreaToDecorationsHint`): los roles `TitleBar`, `MinimizeButton`, `MaximizeButton` y `CloseButton` hacen que Windows la trate como la nativa (arrastre, Snap). Con decoraciones `Full`, Avalonia 12 dibuja encima su propio título y un botón de pantalla completa.
- Cerrar la ventana la deja en la bandeja (preferencia `CloseToTray`, activada por defecto); «Salir de Roost» pregunta si parar el stack, que puede seguir sin la app. La primera vez que la X la oculta (`TrayHintShown`), un aviso dice dónde está el icono: Windows 11 esconde los iconos nuevos tras la flecha ^ y, sin aviso, parece que la app se ha cerrado. Para probarlo sin clics: `PostMessage(WM_CLOSE)` a la ventana, que es lo mismo que la X.
- `InstallDialogViewModel` es el diálogo «Instalando» de PHP, bases de datos y nginx: pasos, progreso, segundo plano y cancelar. Si se pasó a segundo plano, la pantalla espera a `Finished` y avisa del resultado.
- Sitios:
  - el PHP de un sitio se cambia desde el desplegable **con confirmación**, porque rearranca el stack; si no se aplica, el registro vuelve al de antes;
  - la raíz web se cambia desde el panel (el selector, «Usar la carpeta del proyecto» o un chip de subcarpeta típica) con `SetRoot` + `ApplyAsync`;
  - «Crear y abrir» registra, pasa al hosts (UAC con el hosts del sistema), arranca o rearranca, crea la base de datos, **ofrece confiar en la CA y después abre el navegador** (`OfferTrustAsync`, que abre el aviso de Windows: no lo pruebes sin que lo pida el usuario). Si se cancela el permiso del hosts, el sitio queda creado y se dice cómo añadirlo;
  - `MainViewModel.SyncHostsAsync` devuelve `bool` (y `HostsError`) en vez de lanzar, y `ApplyAsync` devuelve `bool`: quien lo llama no sigue como si hubiera ido bien;
  - el panel avisa si la carpeta del proyecto o la raíz web ya no existen, y copia la URL y la ruta.
  - Los tests de vistas nunca deben llegar a `ApplyAsync`: con el estado falso hablarían con un daemon. Desde `1cfdb13` la tubería sale de la raíz de las rutas, así que sería uno de pruebas que no existe, pero aun así se evita. El PHP recomendado sale del `require.php` del composer.json de la carpeta.
- Servidor web: los puertos HTTP/HTTPS se comprueban en vivo (libre, ocupado y por quién, en uso por Roost) y se guardan con `RoostConfig.SetWebPorts`; «Validar» es `StackPlanner.TestNginx` / `TestApache` (el mismo `-t` que hace `up`) y «Recargar» reinicia solo el servidor web. Los radios de «Servidor activo» llaman a `SwitchServerAsync`: instalan el elegido si falta (con el diálogo de instalación), lo guardan con `SetWebServer` y rearrancan el stack si estaba en marcha. La pestaña sigue al servidor activo hasta que el usuario elige otra, y la de Apache solo enseña su detalle cuando está instalado.
- Auditoría: el inventario sale de `AuditState` (filtros por color y «fuera de Roost»); el detalle trae los avisos, las mitigaciones y la acción que toca («Instalar 8.3.36» llama a `PhpViewModel.InstallLatestAsync`). «Exportar» escribe Markdown o JSON con `AuditReport` (Core), el mismo JSON que `roost audit --json`. En los tests, `AuditState.Show` pone hallazgos sin escanear la máquina.
- Ajustes guarda lo de la app en `DesktopPrefs` y lo del stack en roost.toml (`SetWebPorts`, `SetDbPort`, `SetPhpPool`). «Abrir Roost al iniciar sesión» escribe `HKCU\...\Run` (`Services/Autostart`, abre con `--minimized`). «Nombres .test» cambia entre el hosts y el DNS local (`MainViewModel.SetResolverAsync`: un UAC para la regla y, al volver, otro para el hosts). Lo que el mockup marca como futuro (cambiar rutas, avisos en la bandeja) está visible y desactivado.
- Firefox (`Core/Platform/FirefoxTrust`): lee los perfiles de `profiles.ini` y pone `security.enterprise_roots.enabled` en el `user.js` de cada uno, entre las marcas `// roost:begin` y `// roost:end`. Con eso, Firefox lee las CA de confianza de Windows; `CURRENT_USER\ROOT` también, según `EnterpriseRoots.cpp`. Lo usan `roost cert trust`, «Configurar Firefox» en Ajustes, la casilla del diálogo de la CA (`MainViewModel.ConfigureFirefoxAsync`) y «Dejar de confiar» (que lo quita). `ROOST_FIREFOX_DIR` cambia la carpeta de Firefox; `FakeRoost` la apunta a una vacía, así que **los tests nunca tocan el Firefox de verdad**.
- Actualizaciones (`Services/UpdateState`, sobre `UpdateChecker` con el `HttpClient` y la versión de `AppServices`): al construirse lee la caché (el aviso sale sin red) y `AutoCheckAsync` mira al abrir la app y cada 6 h, con la caché de 24 h, si `DesktopPrefs.CheckUpdates` (casilla de Ajustes › Acerca de, activada por defecto) y no hay `ROOST_NO_UPDATE_CHECK`. Con una versión nueva sale un aviso al pie de la barra lateral (lleva a Ajustes), una nota arriba en Ajustes con las novedades, cómo actualizar según `Channel` y los botones, y una entrada en la bandeja. «Buscar actualizaciones» fuerza la consulta. En los tests, `FakeUpstream` sirve una 0.2.0 en la URL de GitHub, `FakeRoost.Version` fija la instalada en 0.1.0 y `UpdateState.Channel` se cambia a mano.
- Bienvenida (`WelcomeDialogViewModel`): la primera vez sin ningún PHP de Roost, o desde «Importar desde Laragon…». Propone las ramas de PHP que se usan fuera, una MariaDB LTS (con otro puerto si el 3306 está cogido) y las carpetas de la www de Laragon como sitios; `MainViewModel.RunWelcomeAsync` lo hace con los mismos diálogos de cada pantalla.
- Registros lee el final de cada fichero de `logs/` (y el de roost-helper) sin bloquearlo, colorea por nivel (`LogsViewModel.LevelOf`) y, con «Seguir», relee cada segundo si crece. `MainViewModel.OpenLogAsync` abre uno concreto desde otra pantalla.
- Pantalla PHP: «Usar como global» guarda la rama si la instalación es la mejor de su rama (así entran solas las versiones nuevas) y, si no, la instalación exacta. Una extensión se activa o se quita con `PhpExtensionChoice` (`etc/php/<instalación>/extensiones.json`, solo lo que se aparta de lo de serie): `StackPlanner.WritePhpIni` rehace ese php.ini y se reinicia solo el pool de esa rama.

### Paquetes (Scoop, winget) y la Action de auditoría (fuera de la solución, en Python)
- Dependen del contrato de las releases: etiqueta `v<semver>`, assets `roost-<versión>-win-x64.zip` (todo en la raíz del zip), `roost-<versión>-linux-x64.tar.gz` (`roost` en la raíz) y `SHA256SUMS.txt` (formato de sha256sum). Si cambian los nombres, cambia `packaging/packages.py`, `bucket/roost.json` (`autoupdate`) y `actions/audit/roost_audit.py`.
- `.github/workflows/packages.yml` se dispara con `workflow_run` cuando «Release» acaba bien (la etiqueta llega en `head_branch`) o a mano con la etiqueta. Las prereleases no van a ningún sitio. Actualiza `bucket/roost.json` con un commit en la rama principal y genera los manifiestos de winget (artefacto); solo los manda a winget-pkgs (`wingetcreate submit`) si existe el secreto `WINGET_TOKEN`.
- `packaging/packages.py` (solo biblioteca estándar): `scoop` y `winget` rellenan el bucket y las plantillas de `packaging/winget/` (esquema 1.12.0, el que usa hoy winget-pkgs); `check` los valida contra los esquemas oficiales (necesita `pip install jsonschema pyyaml`). Para cambiar la descripción o las notas de winget, edita las plantillas, no lo generado. El instalador de winget es zip con portables anidados y `ArchiveBinariesDependOnPath: true`: winget pone la carpeta en el PATH en vez de symlinks, porque `roost.exe` busca `roost-helper.exe` y sus dll a su lado.
- `actions/audit/` es una acción compuesta autocontenida (bash + `roost_audit.py`, Python 3.8+ sin dependencias), lista para copiarla a un repo propio y publicarla en el Marketplace (que pide `action.yml` en la raíz de un repo con una sola acción). No cambia lo que hace `roost audit`: lo ejecuta dos veces (tabla para el log, `--json` para el resumen). `only` filtra por tipo **después**, porque las imágenes de GitHub traen un MySQL 8.0 (EOL) que haría fallar cualquier proyecto PHP.
- Para probarlo sin GitHub: `python packaging/packages.py check` (bucket y plantillas), y `roost_audit.py install --base-url <servidor de pruebas>` o `ROOST_AUDIT_BASE_URL` para servir una release falsa.

## Convenciones

- Comentarios, textos para el usuario y nombres de los métodos de test van en castellano (`Php_8_3_18_Es_Vulnerable`, `Scan_DeduplicaElMismoBinarioVistoPorDosFuentes`). Los comentarios de Roost.Core llevan tildes; los ficheros de Roost.Cli se escriben sin tildes (`auditoria`, `semaforo`). Sigue lo que haga el fichero que estés editando.
- El código de detección no debe lanzar nunca. `IComponentScanner.Scan`, `IProcessRunner.Run` y `Globber.Expand` están definidos para tragarse los errores (rutas que no existen, binarios colgados, permisos) y devolver vacío o null. Un scanner roto se salta y no detiene el escaneo.
- Mantén puros el parseo y la evaluación. Los tests nunca ejecutan binarios reales ni van a la red:
  - inyectan un `IProcessRunner` falso (ver `ScannerPlumbingTests`);
  - construyen `DetectedComponent` a mano (ver `AuditEvaluatorTests`);
  - sirven un zip hecho en memoria con un `HttpMessageHandler` falso, sobre un `RoostPaths` en una carpeta temporal (ver `PhpInstallerTests`, `NginxInstallerTests`, `DbInstallerTests`, donde el handler también da 404 para probar la URL de respaldo);
  - usan un `IProcessLauncher` falso, con procesos que son `TaskCompletionSource` (ver `SupervisorTests`);
  - o un `IProcessRunner` falso que da por bueno el `nginx -t` (o el `httpd -t`), para probar un `Prepare` entero sobre ficheros falsos (ver `SitesTests` y `ApacheTests`, que tampoco tocan el `hosts` real ni el almacén de certificados).

  El daemon, la CLI de `up` y la inicialización real de los datadirs no tienen tests automáticos. Se prueban a mano con `ROOST_HOME`, puertos libres, peticiones HTTP (también una página PHP que se conecta por mysqli) y matando procesos.
- Los textos que ve el usuario (incluidos los mensajes de `RoostException`, aunque vivan en Core, y los ficheros que genera Roost: `roost.toml`, `php.ini`, `nginx.conf`) van sin tildes, como el resto de la salida del CLI. **Excepción: los textos propios de Roost.Desktop llevan tildes**, como el mockup (no pasan por una consola).
- Los puntos de enganche se prueban con una extensión de muestra compilada aparte (`tests/Roost.Extensibility.Sample`): `Roost.Extensibility.Tests` no la referencia, la copia a `bin/sample-extension/` al compilar y la carga desde una carpeta, como una de verdad.
- En XAML de la app, todo `<Run Text="{Binding …}">` lleva `Mode=OneWay`: en Avalonia 12 `Run.Text` es TwoWay por defecto y, con un `StringFormat` sobre una propiedad `init` de un record, el texto formateado se reescribe en la fuente sin fin (desbordamiento de pila al dibujar).
- Las vistas de la app se prueban con Avalonia.Headless (`tests/Roost.Desktop.Tests`, xUnit v3): cada pantalla se monta sobre una raíz falsa (`FakeRoost`, sin daemon: `StackController.Show` le pone un estado) y se dibuja con Skia. El test falla si la vista no carga, y deja una captura PNG para revisarla a ojo.
