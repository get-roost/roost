# Roost

Roost te dice qué versiones de PHP, MySQL y MariaDB tienes instaladas, cuáles están fuera de soporte (EOL) y cuáles tienen CVE abiertos.

Es un CLI y, en Windows, también una app de escritorio con bandeja. La **fase 0** (inventario y auditoría) y la **fase 1** (instalar PHP, MariaDB y MySQL, servirlos con nginx, sitios `https://*.test` y la app de escritorio) están hechas en Windows, y la fase 2 va avanzando. El objetivo es un entorno de desarrollo PHP tipo Laragon que funcione igual en Windows y en Linux y que sepa cuándo caduca lo que instala.

## Por qué

El 21-oct-2026 se publicaron los parches de PHP2Shell (CVE-2026-109618 y CVE-2026-109619): dos RCE sin autenticación en el core de PHP, con CVSS 9.2. Al día siguiente, en una sola máquina con Laragon había seis versiones de PHP vulnerables y dos 7.4 EOL que nadie recordaba, y hubo que escribir un script para saberlo. Ni Laragon, ni Herd, ni XAMPP avisan de nada de esto. Roost es ese script, generalizado y mantenido.

## Requisitos

- SDK de [.NET 10](https://dotnet.microsoft.com/download).
- Windows, Linux o macOS.

## Instalar Roost

Los paquetes salen de las [releases de GitHub](https://github.com/get-roost/roost/releases), que son autocontenidas (no hace falta .NET). **Todavía no hay ninguna**: todo esto funciona a partir de la primera, la v0.1.0. Al publicarla, el workflow `Packages` pone su versión y su SHA-256 en el bucket de Scoop y genera los manifiestos de winget.

```powershell
# Scoop (el bucket es este mismo repositorio)
scoop bucket add roost https://github.com/get-roost/roost
scoop install roost

# winget, cuando Roost.Roost esté aceptado en winget-pkgs
winget install Roost.Roost
```

O a mano: descarga `roost-<versión>-win-x64.zip` (Windows: la CLI, la app y el helper) o `roost-<versión>-linux-x64.tar.gz` (Linux: solo la CLI), compruébalo con el `SHA256SUMS.txt` de la misma release y descomprímelo donde quieras.

- Scoop pone `roost` en el `PATH` y un acceso directo «Roost» a la app en el menú Inicio. winget pone en el `PATH` la carpeta del paquete (`roost` y `roost-desktop`).
- Los datos no viven en la carpeta del paquete, sino en `C:\roost`, `%APPDATA%\Roost` y `C:\www` (ver [Dónde vive todo](#dónde-vive-todo-windows)), así que actualizar o desinstalar no los borra. Antes de actualizar, ejecuta `roost down` y cierra la app: los ficheros en uso no se pueden sustituir.
- **El helper elevado en una carpeta de usuario.** `roost-helper.exe` se ejecuta como administrador (UAC) para editar el `hosts` y la regla DNS de `.test`, y con Scoop, winget o el zip vive en una carpeta que puede escribir cualquier programa de tu usuario. Lo resolverá el instalador; mientras tanto, acepta su UAC solo cuando lo hayas pedido tú. Con `winget install Roost.Roost --scope machine`, desde una consola de administrador, el paquete va a `Program Files`, donde no puede escribir un programa sin permisos.
- Los PHP, MariaDB y MySQL oficiales de Windows necesitan el Visual C++ Redistributable 2015-2022 (`winget install Microsoft.VCRedist.2015+.x64` o `scoop install extras/vcredist2022`).

## Uso

Desde el código fuente, `roost` es `dotnet run --project src/Roost.Cli --`:

```sh
dotnet run --project src/Roost.Cli -- scan            # inventario
dotnet run --project src/Roost.Cli -- audit           # semáforo de seguridad
dotnet run --project src/Roost.Cli -- audit --json    # para CI o scripts
dotnet run --project src/Roost.Cli -- php list        # PHP instalados por Roost y disponibles (Windows)
dotnet run --project src/Roost.Cli -- db list         # MariaDB y MySQL instaladas y disponibles (Windows)
dotnet run --project src/Roost.Cli -- up              # sirve el PHP global con nginx en http://localhost/ (Windows)
```

Lo mínimo para tener PHP y una base de datos sirviendo en Windows:

```sh
roost php install 8.4
roost db install mariadb@11.4    # o mysql@8.4, mysql@5.7...
roost up                         # la primera vez instala nginx; luego abre http://localhost/
roost db create tienda
roost site add tienda            # https://tienda.test/ (pide permiso de administrador para el hosts)
roost cert trust                 # una sola vez: que el navegador confíe en los https://*.test
```

### `roost scan`

Lista cada instalación encontrada, agrupada por tipo, con su versión, origen, SAPI, arquitectura y ruta. El `*` marca la que resuelve el `PATH` (la que se ejecuta al escribir `php` en la terminal).

Dónde busca:

| SO | Fuentes |
|---|---|
| Windows | Lo instalado por Roost (`C:\roost\bin\php`), Laragon, XAMPP, WAMP, chocolatey (`C:\tools\php*`), PHP suelto (`C:\php*`), scoop, Herd, `Program Files` (MySQL y MariaDB) y `PATH`. Barre las unidades C:, D: y E:, y la del perfil del usuario. |
| Linux y macOS | Rutas de la distro (`/usr/bin`, `/usr/sbin`, remi), `/usr/local/bin`, `/usr/local/mysql` y `/opt` (compilados a mano, tarballs oficiales, imágenes Docker `php:*`), Homebrew (PHP) y `PATH` (solo servidores: `mysqld` y `mariadbd`, no los clientes `mysql` ni `mariadb`). Un binario visto por dos enlaces (con usrmerge, `/bin` es `/usr/bin`) sale una vez. |

La versión se obtiene ejecutando el binario (`php -v`, `mysqld --version`), con un límite de 15 s. Si un binario no responde, se salta y el escaneo sigue.

### `roost audit`

Escanea lo mismo y evalúa cada instalación contra los avisos del feed firmado (más abajo, «El feed de avisos»):

| Veredicto | Color | Significa |
|---|---|---|
| VULNERABLE | rojo | Le afecta un aviso del dataset. Indica a qué versión actualizar. |
| EOL | rojo | Su rama está fuera de soporte y no recibe parches oficiales. Tiene prioridad sobre VULNERABLE: la solución es cambiar de rama, no parchear. |
| PARCHEADA | verde | Está al día frente a los avisos conocidos. |
| SOPORTADA | verde | Su rama tiene soporte, pero el dataset no trae CVE de ese producto (hoy, MySQL y MariaDB), así que no se puede afirmar que esté parcheada. |
| DESCONOCIDA | ámbar | El dataset no conoce su rama. |

Lo rojo y lo ámbar se detallan debajo de la tabla, con los avisos (CVSS, si están en el catálogo KEV de CISA), las mitigaciones oficiales y el arreglo real.

**Paquetes de distro: ámbar, no rojo.** Un PHP de Ubuntu como `7.4.3-4ubuntu2.29+esm5` *parece* vulnerable por su número de versión, pero puede tener el parche aplicado mediante un backport. Comparar por número no sirve en ese caso. Por eso, lo instalado por el gestor de paquetes, o lo que lleva sufijos como `+deb`, `+esm` o `ubuntu`, sale en ámbar con el veredicto marcado con `?` (por ejemplo `VULNERABLE?`). Para confirmarlo, consulta `apt changelog <paquete>` o `pro fix`.

**Códigos de salida**, para usarlo en CI o en un cron:

| Código | Significa |
|---|---|
| `0` | Nada bloqueante (puede haber ámbar). |
| `1` | Al menos una instalación VULNERABLE o EOL *con certeza*. |
| `2` | No se pudo cargar el dataset. |
| `3` | Las opciones no valen (por ejemplo, `--only` con un tipo que no existe). |

En la salida `--json`, el campo `blocking` dice lo mismo que el código de salida, y `dataset.origin` dice con qué se ha evaluado: `feed` (firmado), `embedded` o `file`. `--only php` (o `mysql`, `mariadb`, varios separados por comas) audita solo esos tipos: el resumen, `blocking` y el código de salida salen de lo filtrado, y el JSON lo dice en `only` y `skipped`. Sirve en CI, donde la imagen del runner trae su propio MySQL; `roost scan` también lo acepta. Con `--offline` no pregunta por un feed nuevo.

### `roost audit` en la CI (GitHub Actions)

La acción [`actions/audit`](actions/audit/README.md) baja Roost de las releases (Linux x64 o Windows x64), verifica su SHA-256, pasa `roost audit` en el runner y deja el semáforo en el resumen del job. Por defecto, el paso falla con lo que hace salir a `audit` con `1`:

```yaml
      - uses: shivammathur/setup-php@v2
        with:
          php-version: '8.3'
      - uses: get-roost/roost/actions/audit@v0.1.0
        with:
          only: php        # solo cuenta PHP: las imágenes de GitHub traen su propio MySQL 8.0, en EOL
```

Audita el runner entero, no el proyecto. En Linux, los PHP de paquetes (los de `setup-php` en Ubuntu) salen en ámbar y no hacen fallar el job; en Windows, `setup-php` pone los zips oficiales y sí pueden ponerse en rojo. Las entradas, las salidas y los detalles, en [su README](actions/audit/README.md). Funciona a partir de la primera release de Roost.

### `roost php` (solo Windows, por ahora)

Instala PHP oficiales de windows.php.net, cada uno en su propia carpeta, sin tocar los de Laragon ni los de XAMPP:

```sh
roost php list                    # instalados por Roost y disponibles, con su veredicto de seguridad
roost php install 8.4             # la última de la rama 8.4, NTS x64 (el que usa nginx)
roost php install 8.3.36 --ts     # build thread-safe, para Apache
roost php install 7.4 --arch x86  # también ramas EOL, con aviso, para proyectos viejos
roost php remove 8.4.27-nts-x64   # también vale la versión (8.4.27) o la rama (8.4) si no es ambigua
roost php use 8.3                 # PHP global: el que sirve http://localhost/ (si está en marcha, se aplica ya)
```

- **Solo la última de cada rama.** Es lo único que publica el catálogo (`releases.json`); si pides otra, Roost te dice cuál hay.
- **Verificado antes de extraer.** El SHA-256 del zip se compara con el del catálogo; si no coincide, no se instala nada.
- **Sin instalaciones a medias.** Se extrae a una carpeta temporal que se renombra al final. Una instalación existente nunca se pisa, y `remove` solo quita lo que instaló Roost.
- Al terminar arranca `php -v`. Si no responde, avisa de que suele faltar el Visual C++ Redistributable y da el enlace.
- Lo instalado aparece en `scan` y `audit` con origen `Roost`.

### `roost db` (solo Windows, por ahora)

Instala y elige la base de datos: **MariaDB** (cualquier rama estable) o **MySQL** (8.4 LTS, 8.0 y 5.7), cada versión en su carpeta:

```sh
roost db list                    # instaladas y disponibles, con su veredicto de seguridad
roost db install mariadb@11.4    # mariadb a secas: la LTS más nueva; mariadb@13: la 13.x más nueva
roost db install mysql@8.4       # mysql a secas o mysql@8: la 8.4 LTS
roost db install mysql@5.7       # ramas EOL también (8.0, 5.7), con aviso, para proyectos viejos
roost db use mysql@8.4           # la que arranca roost up (si está en marcha, cambia en caliente)
roost db use none                # roost up sin base de datos
roost db create tienda           # CREATE DATABASE tienda, utf8mb4
roost db remove mysql@5.7        # quita los binarios; los datos se quedan
```

- **De dónde salen.** El catálogo de MariaDB se lee de la API oficial de descargas (`downloads.mariadb.org`), que da la última versión de cada rama estable con su SHA-256. Oracle no tiene API para MySQL, así que sus tres versiones van fijadas en Roost: el SHA-256 de cada zip se fijó después de verificar su firma PGP con las claves de MySQL Release Engineering. Como en PHP, el SHA-256 se comprueba antes de extraer nada.
- **Un motor a la vez.** `roost up` arranca solo la base de datos global, la que fija `roost db use`. Sin elegir ninguna, arranca la más nueva que haya instalado Roost (MariaDB antes que MySQL).
- **Datos por motor y rama** en `C:\roost\data\<motor>\<rama>` (por ejemplo `data\mariadb\11.4`, `data\mysql\8.4`). Nunca se comparten entre motores, y una actualización dentro de la rama (11.4.12 → 11.4.13) usa los mismos datos. La primera vez se crean con `root` **sin contraseña**, como en Laragon. Roost nunca borra datos.
- **Solo local.** Escucha en `127.0.0.1` (puerto 3306 por defecto), en utf8mb4. Con MySQL 8 se desactiva el protocolo X (puerto 33060).
- **Parada limpia.** Arranca la primera y se para la última. Al pararla, Roost le pide `shutdown` (`mariadb-admin` / `mysqladmin`) y espera a que cierre sus ficheros; solo si no responde en 30 s, la mata.
- **Si el puerto 3306 está ocupado** (por ejemplo, por el MySQL de Laragon o un servicio de Windows), `roost up` lo dice y no arranca nada. Cambia `[db] port` en `roost.toml`.

### `roost up / down / status` (solo Windows, por ahora)

`roost up` arranca la base de datos global (si hay alguna) y sirve el PHP global con nginx en `http://localhost/`:

- **La carpeta servida** es `C:\www`. Si no existe, Roost la crea con una portada; si ya existe, no la toca. Cada proyecto dentro se ve en `http://localhost/<carpeta>/`.
- **El PHP global** es el que fija `roost php use`. Si no hay ninguno fijado, se usa el más nuevo que haya instalado Roost.
- **php-cgi:** como Windows no tiene php-fpm, arranca un pool de 4 `php-cgi` (por defecto) detrás de nginx, en los puertos `9000 + mayor·100 + menor·10 + i` (la 8.4 usa 9840-9843).
- **nginx 1.30.6:** la primera vez lo descarga de nginx.org y verifica su SHA-256.
- **Solo local:** nginx escucha solo en 127.0.0.1, así que no se ve desde la red.
- **Comprobaciones previas:** si el puerto está ocupado, lo dice y no arranca nada. La configuración de nginx se valida con `nginx -t` antes de arrancar.
- **Si algo no arranca,** `up` lo dice con el final de su log y para el resto. Si es un puerto ocupado, dice qué proceso lo tiene.

Un daemon de usuario (`roost daemon`, que la CLI lanza sola) hace de padre de nginx y de los php-cgi:
- Relanza los que se caen. Un php-cgi que se recicla tras sus 500 peticiones vuelve en el acto; uno que se cae nada más arrancar se marca como fallido a las 5 caídas seguidas.
- Sigue en marcha aunque cierres la terminal.
- Habla con la CLI por una named pipe a la que solo puede conectarse tu usuario, nunca por TCP.
- Si el daemon muere, Windows mata con él a nginx y los php-cgi, así que nunca quedan servidores huérfanos.

```sh
roost up          # arranca, o re-arranca con la configuración actual
roost status      # servicios, PID, puertos y reinicios (también --json)
roost restart nginx   # reinicia un servicio sin tocar el resto: nginx, php-8.4 (todo su pool), mariadb-11.4
roost down        # lo para todo, daemon incluido
```

**Configuración** (`%APPDATA%\Roost\roost.toml`, opcional; `roost php use` la crea si no existe):

```toml
[php]
version = "8.4"   # PHP global: rama, versión (8.4.27) o instalación (8.4.27-nts-x64)
pool = 4          # procesos php-cgi (1 a 10)

[web]
port = 80         # puerto HTTP de nginx, solo en 127.0.0.1
https_port = 443  # puerto HTTPS de los sitios *.test

[db]
engine = "mariadb"  # "mariadb", "mysql" o "none"
version = "11.4"    # mayor (8), rama (11.4) o exacta
port = 3306         # solo en 127.0.0.1; usuario root sin contraseña
```

Cuando un comando cambia una clave, Roost toca solo esa clave y respeta el resto del fichero, comentarios incluidos.

**`php.ini`:** Roost genera uno por instalación en cada `roost up`, con ajustes de desarrollo y las extensiones habituales que traiga ese build (curl, mbstring, openssl, pdo_mysql, intl, zip, OPcache…). Ese no se edita. Tus ajustes van en `C:\roost\etc\php\conf.d\99-usuario.ini`, que se carga en todas las versiones y sobrevive a las actualizaciones.

### La app de escritorio (solo Windows, por ahora)

`roost-desktop` es una ventana al estilo de Laragon sobre lo mismo que la CLI, con el stack en el daemon:

- **Inicio:** accesos a la web, a una terminal con el PHP global en el PATH, a la carpeta www y a la base de datos, y los servicios con su estado, sus puertos, sus reinicios y un botón para reiniciar cada uno.
- **Sitios:** alta con plantilla y el PHP que pide el `composer.json`, el PHP propio de cada sitio, su certificado y su línea en el hosts.
- **PHP:** las versiones instaladas, con su veredicto, quién las usa y sus extensiones (activar una reinicia solo su pool), el catálogo de windows.php.net (NTS/TS, x64/x86) y lo que hay fuera de Roost.
- **Servidor web:** nginx, sus puertos HTTP y HTTPS, `nginx -t` y su configuración.
- **Bases de datos:** la conexión (con copiar y la línea para el `.env`), el puerto con quién ocupa cada uno, MariaDB y MySQL instaladas y disponibles, y las bases de datos del servidor (crear, consola SQL).
- **Auditoría:** todo lo de la máquina frente a los avisos, con sus mitigaciones y una exportación a Markdown o JSON.
- **Registros:** el final de cada log, coloreado, con filtro y en vivo.
- **Ajustes:** inicio con Windows, bandeja, tema claro u oscuro, puertos, la CA local y cómo se resuelven los `.test` (hosts o DNS local).

La primera vez, una bienvenida propone traer las ramas de PHP que ya usas, una MariaDB y las carpetas de la www de Laragon como sitios. Desde la bandeja se arranca y se para todo, se cambia el PHP o la base de datos global y se abren los sitios. La app lleva `roost.exe` (el daemon) y `roost-helper.exe` a su lado.

```sh
dotnet run --project src/Roost.Desktop
```

Cerrar la ventana la deja en la bandeja. El stack lo lleva el daemon, así que sigue en marcha aunque salgas de la app (al salir, pregunta). Lo que está previsto pero aún no hecho (Apache, plantillas distintas de «Vacía», DNS local, Firefox, cambiar las rutas) aparece desactivado y marcado como «próximamente».

### `roost shims`: el `php` de la terminal según la carpeta (solo Windows, por ahora)

```sh
roost shims install        # pone C:\roost\bin\shims (con su php.exe) delante en el PATH de tu usuario
roost php use 7.4 --here   # este proyecto, en 7.4: escribe un .php-version en la carpeta
roost php which            # que php toca aqui y por que
roost shims remove         # lo deja como estaba
```

En una terminal nueva, `php` usa, por este orden: la variable `ROOST_PHP_VERSION`, el `.php-version` más cercano (en la carpeta o en una superior), el PHP del sitio cuya carpeta es esta, o el PHP global. Carga el mismo `php.ini` que el servidor, así que Composer y PHPUnit tienen las mismas extensiones. Si hay otro `php.exe` en el PATH del sistema (Laragon, XAMPP), Windows lo mira antes y gana: `roost shims status` lo avisa. En la app, es una casilla de Ajustes.

### `roost site` y `roost cert` (solo Windows, por ahora)

Cada proyecto puede tener su sitio `https://<nombre>.test/`, con su vhost, su certificado y, si quiere, su propio PHP:

```sh
roost site add tienda                                    # carpeta C:\www\tienda (se crea si no existe)
roost site add legacy --path C:\proyectos\legacy --php 7.4
roost site add legacy --php global                       # vuelve al PHP global
roost site add api --root web                            # raíz web distinta (por defecto, public si existe)
roost site list                                          # carpeta, PHP, caducidad del certificado, si está en el hosts (o DNS)
roost site remove legacy                                 # la carpeta del proyecto no se toca
roost cert trust                                         # que Chrome y Edge confíen en la CA de Roost (una vez)
roost cert renew                                         # vuelve a emitir los certificados (up ya los renueva solo)
```

- **Repetir `site add` actualiza el sitio,** sin romper nada. Si el stack está en marcha, el cambio se aplica en el acto.
- **Nombres y certificado.** Se sirven `tienda.test` y cualquier subdominio (`api.tienda.test`), en HTTP y HTTPS. Escucha solo en 127.0.0.1, por los puertos de `[web]` (80 y 443 por defecto). La raíz web es `public/` si existe (Laravel, Symfony); si no, la carpeta.
- **PHP por sitio.** Cada rama de PHP en uso tiene su propio pool de php-cgi.
- **Tu vhost.** Cada sitio tiene su `C:\roost\etc\nginx\sites\<nombre>.conf`. Puedes editarlo **fuera** de las marcas `# roost:begin` / `# roost:end`, por ejemplo el `location /` del controlador frontal o reglas tuyas: Roost solo regenera lo de dentro. Al quitar el sitio, el fichero se aparta como `.conf.removed`.
- **El `hosts`.** Roost escribe en el `hosts` de Windows un bloque `# roost:begin` / `# roost:end` con una línea `127.0.0.1 <nombre>.test` por sitio. Lo hace `roost-helper.exe`, lo único de Roost que se ejecuta como administrador:
  - solo sabe hacer eso;
  - rechaza cualquier nombre que no sea `<algo>.test`;
  - deja intacto lo de fuera del bloque, byte a byte;
  - anota cada ejecución en `%ProgramData%\Roost\roost-helper.log`.

  Windows solo pide permiso (UAC) cuando el bloque tiene que cambiar. Con el DNS local (abajo), el hosts no se toca.
- **La CA local.** Roost tiene su propia CA (ECDSA P-256, 10 años) en `C:\roost\certs\ca`. Su clave privada solo la puede leer tu usuario. Con ella firma un certificado por sitio, de 397 días, que `roost up` renueva cuando le quedan menos de 30. `roost cert trust` la añade al almacén de **tu usuario** (no al de la máquina), y Windows te pide confirmarlo. Firefox usa su propio almacén: activa `security.enterprise_roots.enabled` en `about:config`.
- **Con el `curl` de Windows** (Schannel) añade `--ssl-no-revoke`. Los certificados de una CA local no tienen lista de revocación, y Schannel la exige por defecto. Los navegadores no la piden.

### `roost dns`: los `.test` sin tocar el hosts (solo Windows, por ahora)

```sh
roost dns install    # una regla de Windows manda los .test al DNS de Roost (un permiso, una sola vez)
roost dns status     # el modo, la regla y si el DNS contesta
roost dns remove     # quita la regla y vuelve al hosts
```

Con el DNS local, `roost site add` y `roost site remove` ya no piden permiso. Los `.test`, con sus subdominios, los responde un servidor DNS muy pequeño que va dentro de roostd, en `127.53.0.1`, mientras el stack está en marcha.

Windows solo le pregunta por `.test`, gracias a una regla NRPT con el comentario «Roost»; el resto de nombres sigue por tu DNS de siempre. La regla la pone `roost-helper.exe` con un script fijo, y Roost no pisa una regla de `.test` que sea de otra herramienta.

Es opcional (`[sites] resolver` en `roost.toml`): por defecto se usa el `hosts`, que también funciona con el stack parado. En la app, está en Ajustes › Nombres .test.

### `roost update`: saber si hay una Roost nueva

nginx, las versiones de MySQL y el dataset de avisos van fijados dentro de Roost, así que lo nuevo de ellos solo llega con una Roost nueva. Roost te avisa, pero no se actualiza solo (eso llegará con el instalador):

```sh
roost update          # pregunta ya a GitHub: la versión que tienes, la última, sus novedades y cómo actualizar
roost update --json   # lo mismo para scripts (código 0 si se pudo comprobar, 1 si no)
```

- **Cómo actualizar** depende de cómo instalaste Roost, y `roost update` lo detecta: con Scoop, `scoop update roost`; con winget, `winget upgrade Roost.Roost`; desde el zip, baja el de la release (`roost-<versión>-win-x64.zip`) y sustituye con él la carpeta. Antes, sal de la app y para el stack (`roost down`): mientras corren, sus ficheros están en uso.
- **La app** lo mira sola al abrirse y, si sigue en la bandeja, una vez al día. Si hay una versión nueva, lo dice al pie de la barra lateral, en el menú de la bandeja y arriba en Ajustes, con las novedades y el comando para actualizar. Se apaga con la casilla de Ajustes › Acerca de Roost.
- **`roost status`** añade una línea si la última comprobación (de `roost update` o de la app) encontró una versión nueva. Para eso no va a la red.
- **Una consulta al día como mucho.** La respuesta se guarda 24 h en `%LOCALAPPDATA%\Roost\cache\update-check.json`. Solo se pregunta a `api.github.com`, sin cuenta y sin más datos que la versión de Roost en el User-Agent. Las prereleases (`v0.2.0-beta.1`) no se anuncian.
- **`ROOST_NO_UPDATE_CHECK=1`** apaga lo automático (la app y la línea de `roost status`), por ejemplo en CI. `roost update` y el botón «Buscar actualizaciones» de Ajustes siguen funcionando, porque los pides tú.

### Dónde vive todo (Windows)

| Qué | Ruta |
|---|---|
| PHP, nginx y bases de datos instalados | `C:\roost\bin\php\<versión>-<nts\|ts>-<arq>\`, `C:\roost\bin\nginx\<versión>\`, `C:\roost\bin\mariadb\<versión>\`, `C:\roost\bin\mysql\<versión>\` |
| Datos de las bases de datos (uno por motor y rama) | `C:\roost\data\<motor>\<rama>\` |
| Configuración generada (`nginx.conf`, un `php.ini` por instalación, un `my.ini` por motor y rama) y `conf.d\` | `C:\roost\etc\` |
| El vhost de cada sitio (editable fuera de las marcas) | `C:\roost\etc\nginx\sites\<nombre>.conf` |
| CA local y certificados de los sitios | `C:\roost\certs\ca\`, `C:\roost\certs\sites\` |
| Sitios registrados | `C:\roost\state\sites.json` |
| Logs (daemon, nginx, cada php-cgi) | `C:\roost\logs\` |
| Proyectos | `C:\www\` |
| Tu configuración | `%APPDATA%\Roost\roost.toml` |
| Zips descargados (se reutilizan) | `%LOCALAPPDATA%\Roost\cache\downloads\` |

La variable de entorno `ROOST_HOME` lo mete todo bajo una carpeta, para probar sin tocar `C:\roost` ni `C:\www`: binarios, configuración, `www` y `roost.toml` van directamente en `ROOST_HOME`, y la caché en `ROOST_HOME\cache`.

## El feed de avisos (`roost-catalog`)

`roost audit` y la pantalla Auditoría evalúan contra un feed firmado que se regenera cada día:

- **Las fuentes** son endoflife.date (ramas y fechas de fin de vida de PHP, MySQL, MariaDB y nginx), php.net (la última de cada rama), los avisos de seguridad de GitHub de `php/php-src` y el catálogo KEV de CISA (explotación activa).
- **Lo que no se automatiza** está en [catalog/](catalog/): nombres públicos como «PHP2Shell», títulos en castellano y mitigaciones.
- **El generador** es `src/Roost.Catalog` y lo lanza [.github/workflows/catalog.yml](.github/workflows/catalog.yml). El resultado va a la rama [`catalog`](../../tree/catalog) de este repo.
- **La firma** es ECDSA P-256. Roost lleva dentro la clave pública y no usa un feed cuya firma no cuadre, ni uno más viejo que el que ya tiene. Para comprobarla a mano:

  ```sh
  openssl dgst -sha256 -verify roost-catalog.pub.pem -signature advisories.json.sig advisories.json
  ```

- **Roost pregunta como mucho una vez al día** y evalúa siempre en local. Sin red, usa el último feed bueno o el dataset que lleva dentro ([src/Roost.Core/Audit/advisories.json](src/Roost.Core/Audit/advisories.json)), el más nuevo de los dos, y lo dice.

```sh
roost catalog status                                   # qué feed hay, cuál va dentro de Roost y cuál se usa
roost catalog update                                   # bajarlo ya
roost audit --offline                                  # sin preguntar por uno nuevo
roost audit --dataset ruta/a/advisories.json           # con un fichero propio (sin firma)
```

`ROOST_CATALOG_URL` cambia la URL del feed (un espejo); la firma se comprueba igual.

## Limitaciones actuales

- No evalúa CVE de MySQL ni de MariaDB, solo si su rama está en EOL según la fecha.
- Lista las mitigaciones, pero no comprueba si están aplicadas en el `php.ini` efectivo.
- Todavía no detecta nginx ni Apache.
- Los paquetes de distro no se contrastan con los trackers de seguridad de Debian ni de Ubuntu: salen siempre en ámbar.
- El feed aún no trae CVE de MySQL, MariaDB ni nginx (NVD y los boletines de Oracle y MariaDB vendrán después).
- `roost php`, `roost db`, `roost site` y `roost up` solo funcionan en Windows: en Linux no hay binarios oficiales de PHP y los builds propios aún no existen.
- `roost up` arranca una sola base de datos a la vez (la global): no hay base de datos por sitio.
- Cambiar un sitio rearranca todo el stack, base de datos incluida (un par de segundos), en vez de recargar solo nginx.
- Por defecto, los nombres `.test` se resuelven por el `hosts`, y cada alta o baja de un sitio pide UAC. Con el DNS local (`roost dns install`) ya no lo pide, pero los `.test` solo resuelven con el stack en marcha.
- Las versiones de MySQL van fijadas en Roost: para tener una nueva (8.4.12…), hay que actualizar Roost.
- La app de escritorio no tiene aún Apache, plantillas de proyecto (Laravel, WordPress…) ni configuración de Firefox.

## Desarrollo

```sh
dotnet build Roost.slnx
dotnet test Roost.slnx
dotnet test tests/Roost.Core.Tests --filter "FullyQualifiedName~AuditEvaluatorTests.Php_8_3_18_Es_Vulnerable"   # un solo test
```

| Proyecto | Qué contiene |
|---|---|
| `src/Roost.Core` | La detección (scanners, parseo de versiones, modelos), la auditoría (evaluador y dataset), la instalación (PHP, nginx, MariaDB y MySQL), `roost.toml`, el supervisor de procesos y la generación de `php.ini`, `nginx.conf` y `my.ini`. |
| `src/Roost.Daemon` | El daemon (Kestrel en una named pipe) que mantiene el stack, y su cliente. |
| `src/Roost.Extensibility` | El contrato de las extensiones y su cargador: servicios, avisos, scanners, comandos y pantallas que se enchufan desde `bin/extensions`. |
| `src/Roost.Shim` | `roost-shim.exe`, el `php` de la terminal: elige el PHP de la carpeta y lo ejecuta. Se publica con NativeAOT. |
| `src/Roost.Helper` | `roost-helper.exe`, lo único que se ejecuta como administrador: reescribe el bloque de Roost del `hosts` y pone o quita la regla de DNS de `.test`. Sin dependencias. |
| `src/Roost.Cli` | Los comandos y la salida (tablas o JSON). |
| `src/Roost.Desktop` | La app de escritorio (Avalonia): la ventana, la bandeja y los diálogos. Sin lógica propia: usa Core y el daemon. |
| `tests/` | Tests con xUnit. No ejecutan binarios reales ni van a la red: usan salidas de ejemplo, componentes construidos a mano, un servidor HTTP falso y un lanzador de procesos falso. Las vistas de la app se dibujan en headless sobre una raíz de Roost falsa y dejan una captura de cada pantalla. |

Con el stack en marcha, el daemon (`roost.exe daemon`) tiene bloqueada la dll de `bin/`: ejecuta `roost down` antes de recompilar.

[CLAUDE.md](CLAUDE.md) explica la arquitectura con más detalle.

### Publicar una versión

La versión sale de un solo sitio, `<Version>` en [Directory.Build.props](Directory.Build.props). La llevan los ejecutables, `roost --version`, la app (en Ajustes) y los paquetes NuGet.

1. Sube `<Version>` (semver: `0.2.0`, o `0.2.0-beta.1` para una prerelease) y haz commit.
2. Etiqueta ese commit con la misma versión y sube la etiqueta: `git tag v0.2.0` y `git push origin v0.2.0`.

La etiqueta dispara [release.yml](.github/workflows/release.yml), que hace esto:

- Comprueba que la etiqueta coincide con `<Version>`. Si no coincide, falla sin publicar nada.
- Compila y pasa los tests en Windows.
- Publica para Windows con `build/publish-windows.ps1`. La CLI, la app, `roost-helper.exe` y `roost-shim.exe` (este con NativeAOT) van autocontenidos en una sola carpeta y comparten el runtime de .NET. No son single-file, porque cada exe llevaría su propia copia del runtime. El resultado es `roost-<versión>-win-x64.zip`, unos 64 MB.
- Publica la CLI para Linux con `build/publish-linux.sh`. Es un solo ejecutable, `roost`, y el resultado es `roost-<versión>-linux-x64.tar.gz`, unos 45 MB.
- Prueba lo publicado (`--version`, `scan`, `audit` y el shim) antes de empaquetarlo.
- Crea la release «Roost \<versión\>» con los dos ficheros, `SHA256SUMS.txt`, attestations de procedencia de GitHub y notas generadas. Si la versión lleva guion, la marca como prerelease.

Si se lanza a mano (Actions → Release → Run workflow), hace todo menos publicar y deja el resultado en el artefacto `release-preview`. Los mismos scripts sirven en local:

```sh
powershell -File build/publish-windows.ps1   # en artifacts/win-x64; el shim necesita las herramientas de C++ de Visual Studio
bash build/publish-linux.sh                  # en artifacts/linux-x64; también desde Windows
```

**Firma.** Si no se configura nada, la release sale sin firmar y sus notas lo dicen (SmartScreen avisará al abrirla). El workflow firma los exe y las dll de Roost, no los de terceros, con el primer servicio que encuentre configurado en Settings → Secrets and variables → Actions:

| Servicio | Variables | Secretos |
|---|---|---|
| Azure Artifact Signing (antes Trusted Signing) | `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, `AZURE_CERTIFICATE_PROFILE` | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` y `AZURE_SUBSCRIPTION_ID`, de una aplicación de Entra ID con una credencial federada (OIDC) para este repositorio |
| SignPath (gratis para open source con SignPath Foundation) | `SIGNPATH_ORGANIZATION_ID` y, si no valen los de por defecto, `SIGNPATH_PROJECT_SLUG` (`roost`), `SIGNPATH_SIGNING_POLICY_SLUG` (`release-signing`) y `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | `SIGNPATH_API_TOKEN` |

La configuración de artefacto que hay que dar de alta en SignPath está en [.github/signpath/artifact-configuration.xml](.github/signpath/artifact-configuration.xml).

**Paquetes NuGet.** `Roost.Core`, `Roost.Daemon` y `Roost.Extensibility` son paquetes, para que las extensiones compilen contra ellos; el resto de proyectos no se empaqueta. Para generarlos en local: `dotnet pack Roost.slnx -c Release -o artifacts/nuget`. Se publican a mano con [nuget.yml](.github/workflows/nuget.yml): se lanza sobre la etiqueta de la versión, marcando «Publicar». Usa el trusted publishing de nuget.org (la variable o el secreto `NUGET_USER` y una política para `nuget.yml`) o, si no, el secreto `NUGET_API_KEY`.

## Hoja de ruta

Por fases:

- **Fase 0:** hecha: `scan` y `audit` por CLI y el feed firmado `roost-catalog`.
- **Fase 1, MVP:** hecha en Windows: instalar PHP, MariaDB y MySQL, servirlos con nginx desde un daemon de usuario con supervisor, sitios `https://*.test` con certificados de una CA local, y la app de escritorio. Falta Linux y la firma de los binarios; las releases ya salen de un workflow, pero aún no se ha publicado ninguna.
- **Fase 2 (en curso):** hechos en Windows los shims de terminal (`php` según el proyecto en el que estés), los puntos de enganche para extensiones y el DNS local. Los paquetes de Scoop y winget están preparados y esperan a la primera release. Faltan Xdebug con un clic, las plantillas y el instalador.
- **Fase 3:** macOS, ARM y Apache.

Roost es y seguirá siendo gratis y MIT. Más adelante habrá un **Roost Pro** de pago, aparte, para quien lo usa en su trabajo o en equipo: varias bases de datos y servicios a la vez (PostgreSQL, Redis, Mailpit), auditoría de servidores por SSH y un feed de avisos ampliado. Nada de lo que hoy es gratis pasará al Pro, y la seguridad básica (EOL y avisos críticos) llega a todos igual.

## Licencia

MIT. Ver [LICENSE](LICENSE).
