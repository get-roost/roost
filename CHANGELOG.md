# Novedades de Roost

Lo que cambia en cada versión. Es lo mismo que enseña la app en **Novedades** y lo que llevan las notas de cada release en GitHub.

## 0.1.5 — en preparación

- **Xdebug con un clic:** en la pantalla PHP, cada versión tiene un interruptor de Xdebug. La primera vez baja de xdebug.org la DLL que toca a esa versión (comprobada con su SHA-256) y la activa; si Roost está en marcha, reinicia solo esa versión de PHP, sin tocar el resto. Viene apagado. Depura la petición que lo pide (`XDEBUG_TRIGGER` o la extensión del navegador) y conecta con el editor en el puerto 9003; «Copiar launch.json» da la configuración de VS Code. También en la terminal: `roost php xdebug on|off|status [versión]`.
- **Ajustes de PHP solo para una versión:** además de `conf.d\99-usuario.ini`, que vale para todas, cada versión tiene su `conf.d\<versión>\99-usuario.ini`. Lo que pongas ahí no afecta a las demás versiones. Lo leen el servidor y la terminal.
- **Ajustes rápidos de PHP:** `memory_limit`, `upload_max_filesize`, `post_max_size` y `max_execution_time` de cada versión, desde la pantalla PHP o con `roost php ini <versión> --set memory_limit=1G`. Roost comprueba que tengan sentido (por ejemplo, que `post_max_size` deje pasar la subida) y reinicia solo esa versión.
- **Ver el php.ini, phpinfo() y los módulos:** la pantalla PHP enseña el php.ini que genera Roost, phpinfo() y la lista de módulos de cada versión, con buscador.
- **Extensiones desde la terminal:** `roost php ext list|enable|disable <extensión> [--php <versión>]`, las mismas que activa la app. Si PHP no puede cargar una extensión (le falta una DLL de fuera), no se activa y Roost dice por qué, en la terminal y en la app.
- **Arreglado:** OPcache y Xdebug se cargan siempre como `zend_extension`. Antes, un Xdebug copiado a mano en `ext\` y activado desde la app dejaba esa versión de PHP sin arrancar.
- **Redis:** Roost trae Redis 8.10 para la caché, las colas y las sesiones de Laravel o WordPress: la build de redis-windows (Redis de verdad, compilado del código oficial), verificada con su SHA-256 y solo en este equipo. Se instala y se pone en el stack desde Componentes › Redis o con `roost redis install`; arranca después de la base de datos, se para limpio (lo guarda todo en disco antes de salir) y sus datos no los borra nada. Con predis (`composer require predis/predis`) no hace falta ninguna extensión de PHP.
- **Correo de pruebas:** con Mailpit, el correo que mandan tus proyectos (`mail()` de PHP, Laravel, WordPress) se queda en una bandeja de tu equipo en vez de salir a internet. Componentes › Correo o `roost mail install`, y «Abrir la bandeja» o `roost mail open`. El `mail()` de PHP va a Mailpit en todas las versiones, también en la terminal.
- **Adminer:** «Abrir Adminer» en Bases de datos abre `http://localhost/adminer` con el servidor y root ya puestos. La primera vez lo instala (un solo fichero, verificado), fuera de tu carpeta www. Solo atiende a localhost.
- **Servicios de Windows que se quedan los puertos:** un MySQL o una MariaDB instalados con su instalador, Apache, IIS u otro Redis que arrancan con Windows le quitan el puerto a Roost. Auditoría los enseña con cómo dejarlos en Manual (y un botón a Servicios de Windows); la bienvenida avisa y propone otro puerto para la base de datos; `roost up` avisa antes de arrancar y, si un puerto está ocupado por uno de ellos, el error dice cuál. Roost nunca cambia servicios de Windows por su cuenta.
- **Repositorios privados:** `roost site clone` entra ya en repositorios privados: con un token HTTPS (`--token`), con una clave SSH que Roost crea solo para ese sitio (`--ssh`: te enseña la pública para que la pegues como deploy key de solo lectura), con un fichero de clave tuyo (`--ssh-key`) o con tus claves y el ssh-agent de Windows (`--own-ssh`). Si no dices cuál y el repositorio lo pide, Roost te pregunta en la terminal. Si el clonado no acaba, ni el token que acabas de dar ni la clave que acabas de copiar se quedan guardados.
- **Tokens a buen recaudo:** se guardan en el Administrador de credenciales de Windows, cifrados para tu usuario y solo para ese repositorio (o para un grupo o un servidor entero, si lo eliges). Nunca van en la dirección del repositorio, en la línea de órdenes ni en los registros. `roost git token set | remove | list`; la lista nunca enseña el token.
- **Una clave SSH por sitio:** ed25519, solo legible por tu usuario. `roost git key new | show | remove`; `show` te da la clave pública y el enlace a las deploy keys de ese repositorio en GitHub, GitLab o Bitbucket.
- **Huellas de los servidores:** Roost trae de serie las de GitHub, GitLab y Bitbucket, comprobadas con las que publica cada uno, y nunca se fía de otro servidor sin enseñarte su huella (`roost git hosts add git.acme.es`). Si un servidor conocido presenta otra huella, Roost no conecta y te dice la de antes y la de ahora.
- **Errores claros con el acceso:** distingue un token caducado o sin permiso, una organización que exige autorizarlo (SSO), la contraseña de la cuenta en lugar de un token y una clave que no está en las deploy keys del repositorio, y te dice qué hacer en cada caso.
- **Quitar un sitio quita su acceso:** `roost site remove` quita también su clave SSH de Roost y su token, salvo los que use otro sitio (`--keep-access` para dejarlos). El historial y el registro de git se quedan.
- **Cambiar de servidor web:** pasar de Apache a nginx con Roost en marcha ya funciona (antes había que pararlo).

## 0.1.4 — 24-oct-2026

- **Stack:** un cambio que no vale (un puerto ocupado, un vhost sin marcas, una configuración que nginx o Apache no aceptan) ya no deja Roost parado: se comprueba antes de parar nada y lo que estaba en marcha sigue igual.
- **Servicios:** se puede parar y volver a iniciar un servicio suelto (la base de datos, un PHP, el servidor web) desde Inicio, desde Bases de datos y con `roost stop` / `roost start`; un servicio parado a mano no se relanza solo. «Reiniciar todo» (o `roost restart` sin más) reinicia el stack entero en orden.
- **Bases de datos:** «Parar» para de verdad la base de datos, sin cambiar la configuración; antes la quitaba del stack.
- **localhost:** nginx y Apache escuchan también en `[::1]`, así `http://localhost` nunca acaba en otro servidor de la máquina; el DNS local contesta también la dirección IPv6.
- **Certificados:** Roost renueva solo los certificados de los sitios que caducan pronto, aunque lleve semanas en marcha.
- **Arreglo:** `roost down` seguido en el acto de `roost up` ya no deja el stack parado.
- **Composer incluido:** Roost trae Composer 2.10.3, verificado con su SHA-256, y lo instala la primera vez que hace falta. En la terminal, con los shims puestos, `composer` usa el PHP de cada carpeta igual que `php`; mientras no instales el de Roost, tu `composer` sigue siendo el tuyo. `roost composer status` avisa si otro Composer, como el de Composer-Setup, le gana.
- **Composer en Sitios:** si el proyecto tiene `composer.json`, su panel tiene «Instalar dependencias», «Actualizar» y «Añadir paquete…», con el PHP del sitio y la salida en directo (se puede cancelar y copiar). Avisa si falta `vendor/` y de que Composer ejecuta los scripts del proyecto. Desde la terminal, `roost site composer tienda install`.
- **La base de datos de cada sitio:** desde su panel puedes crearla, abrir la consola dentro de ella o copiar su línea del `.env`. Si la base de datos está parada o no hay ninguna, el panel lo dice y te ofrece arrancarla.
- **Nuevo sitio más listo:** reconoce Laravel, Symfony y WordPress por sus ficheros y pone su raíz web; si eliges primero la carpeta, el nombre sale de ella; la base de datos puede ser una nueva, una que ya tienes o ninguna; e «Instalar otra versión» de PHP ya no cierra el diálogo ni pierde lo que habías escrito.
- **Vite en HTTPS:** `roost cert path tienda` te da el certificado y la clave del sitio para el servidor de desarrollo; la guía está en el README.

## 0.1.3 — 24-oct-2026

- **Actualizar Roost desde la app:** el aviso de versión nueva abre «Roost X disponible» con las novedades y lo que pesa. «Actualizar ahora» descarga el instalador, comprueba su SHA-256 con el de la release y cierra Roost un momento; Windows pide permiso una vez y Roost vuelve sola, con el stack en marcha si lo estaba. Si cancelas el permiso, vuelve y te dice que no se ha actualizado. Con Scoop o winget abre una terminal que actualiza y la vuelve a abrir; desde la CLI, `roost update --install`.
- **Sitios desde git:** `roost site clone <url>` crea un sitio clonando un repositorio público de GitHub, GitLab, Bitbucket o de tu servidor (o una carpeta local). Se clona en una carpeta temporal y solo al acabar pasa a ser el sitio: si algo falla, no queda nada a medias.
- **Actualizar y cambiar de rama sin sustos:** `roost site pull` solo avanza (fast-forward) y `roost site branch` solo cambia de rama si no hay cambios locales; si los hay, Roost lo dice y no toca nada. `roost site git` enseña la rama, si vas por detrás, el último commit y el historial; con `--adopt`, adopta un proyecto que ya tenía su `.git`.
- **Un git propio y verificado:** Roost descarga MinGit (el git de Git for Windows para aplicaciones) la primera vez que hace falta, comprueba su SHA-256 y lo usa con una configuración aislada: tu `.gitconfig` no se lee ni se toca. `roost git install` y `roost git status`; `[git] exe` en `roost.toml` para usar otro git. De momento, repositorios públicos y locales, y desde la CLI; los privados y la app llegan en las próximas versiones.
- **Bases de datos:** pantalla rehecha. El puerto se cambia en un solo sitio (junto a la conexión), con quién ocupa cada puerto.
- **Contraseña de root:** ponla o cámbiala desde la app o con `roost db password`; Roost la recuerda para sus propios comandos y la añade a la línea del `.env`.
- **Usuarios y bases de datos:** crea usuarios con acceso a una base de datos o a todas, cámbiales la contraseña o bórralos; borra bases de datos (nunca las del propio servidor). En la CLI, `roost db user` y `roost db drop`.
- **Tus ajustes de MariaDB o MySQL** (`sql_mode` para proyectos viejos, `max_allowed_packet`…) en un fichero tuyo que Roost no toca, con «Editar mis ajustes».
- **HeidiSQL y DBeaver:** si los tienes, la app los abre ya conectados.
- **Después de instalar una base de datos,** Roost comprueba que arranca y, si falta el Visual C++ Redistributable, lo dice con su enlace.
- **Sitios:** botón para cambiar la raíz web (docroot), con accesos rápidos a `public`, `web` o `dist`, y campo «Raíz web» al crear un sitio. También `roost site root`.
- **Sitios con Apache:** quitar un sitio ya no impide el siguiente arranque, y «Editar vhost» abre el fichero que de verdad usa Apache.
- **Sitios:** si cancelas el permiso de Windows, el mensaje dice la verdad (el sitio está creado o quitado; falta el hosts) y cómo arreglarlo; cambiar el PHP de un sitio pide confirmación; botones para copiar la URL y la ruta, y aviso si la carpeta ya no existe.
- **Certificados:** al crear un sitio, Roost ofrece confiar en su CA antes de abrir el navegador, así que lo primero que ves ya no es un error de certificado.
- **Firefox:** también confía en tus sitios `.test`. «Configurar Firefox» en Ajustes, la casilla del diálogo de la CA y `roost cert trust` lo preparan en todos tus perfiles; reinicia Firefox para que lo aplique.
- **Visual C++ Redistributable:** el instalador lo pone si falta o es antiguo, y la app y la CLI avisan antes de instalar PHP o el servidor web si falta.
- **Desinstalar limpio:** desinstalar Roost quita también lo que puso en Windows (la regla de DNS de `.test`, su bloque del `hosts`, los shims del PATH y el arranque con Windows) y pregunta si quitar la CA de tus certificados de confianza. Tus proyectos y bases de datos se quedan. Y `roost cert untrust` deja de confiar en la CA cuando quieras.
- **Scoop:** actualizar y desinstalar paran el stack (`roost down`).
- **php de la terminal:** tras actualizar Roost, la app renueva el shim si es de otra versión.
- **Servidor web:** si instalar nginx o Apache en segundo plano falla, la app te lo dice.
- **Novedades:** esta pestaña, debajo de Ajustes, con lo que cambia cada versión.
- **Proyectos en `C:\roost\www`:** la carpeta de proyectos por defecto pasa a estar dentro de la raíz de Roost. Los sitios que ya tenías siguen donde están.
- **PHP y HTTPS:** la extensión curl de PHP (Guzzle, Laravel, WordPress) ya confía en los certificados de internet y en los de tus sitios `.test`.
- **Xdebug y tareas largas:** nginx y Apache esperan a PHP hasta 10 minutos; parado en un breakpoint ya no sale un 504 al minuto.
- **Bienvenida:** también propone las versiones antiguas de PHP que usas (marcadas como fin de vida), y cada proyecto de Laragon se registra con el PHP que pide su `composer.json`.
- **Ajustes:** las casillas vuelven atrás si algo falla; avisa de un puerto ocupado antes de guardar; «Vaciar» la caché la vacía entera; «Comprobar ahora» dice si había avisos nuevos.
- **`roost php remove`:** ya no quita el PHP global ni uno que necesite un sitio.
- **Textos:** donde decía «nginx» y también vale Apache, ahora dice «servidor web»; Inicio ya no dice «Todo en marcha» si hay algo caído.
- **Registros:** con «Seguir», los ficheros nuevos y los contadores aparecen solos.
- **Linux y macOS:** los comandos del stack explican lo mismo: de momento, en esos sistemas Roost es `roost scan` y `roost audit`.

## 0.1.2 — 23-oct-2026

- **Apache, además de nginx:** `roost web use apache` o Servidor web en la app. Lee los `.htaccess` (WordPress, Laravel…), con HTTPS, HTTP/2 y el mismo PHP por sitio.
- **Barra de título:** minimizar, maximizar y cerrar vuelven a funcionar.

## 0.1.1 — 23-oct-2026

- **Instalador de Windows:** `roost-X-win-x64-setup.exe`, en Program Files, con accesos directos y, si quieres, `roost` en el PATH. Para Roost antes de actualizar o desinstalar y nunca toca tus datos.
- **La X deja Roost en la bandeja** y, la primera vez, dice dónde se ha quedado.
- **Aviso de versión nueva** también para quien usa solo la CLI.
- **Iconos** en los cuatro ejecutables.
- **`roost audit --only`** para auditar solo PHP, MySQL o MariaDB; la GitHub Action de auditoría lo usa.
- **Linux:** el inventario reconoce bien el origen de cada PHP y no repite los que llegan por enlaces.

## 0.1.0 — 23-oct-2026

- **Primera versión pública.** CLI (`roost`) y app de escritorio con bandeja para Windows; en Linux, `scan` y `audit`.
- **PHP, nginx, MariaDB y MySQL** oficiales, verificados antes de instalar, con un PHP global y uno propio por sitio.
- **Sitios `https://<nombre>.test`** con certificados de una CA local, por el `hosts` o por el DNS local.
- **`php` en la terminal según la carpeta** (shims y `.php-version`).
- **Auditoría** de lo que tienes instalado (Roost, Laragon, XAMPP…) frente a avisos de seguridad y fin de vida, con un feed firmado que se actualiza cada día.
- **Extensiones** para añadir servicios, comprobaciones, comandos y pantallas.
