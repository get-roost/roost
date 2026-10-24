# Novedades de Roost

Lo que cambia en cada versión. Es lo mismo que enseña la app en **Novedades** y lo que llevan las notas de cada release en GitHub.

## 0.1.3 — en preparación

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
- **Actualizar Roost desde la app:** el aviso de versión nueva abre «Roost X disponible» con las novedades y lo que pesa. «Actualizar ahora» descarga el instalador, comprueba su SHA-256 con el de la release y cierra Roost un momento; Windows pide permiso una vez y Roost vuelve sola, con el stack en marcha si lo estaba. Si cancelas el permiso, vuelve y te dice que no se ha actualizado. Con Scoop o winget abre una terminal que actualiza y la vuelve a abrir; desde la CLI, `roost update --install`.
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
- **Sitios desde git:** `roost site clone <url>` crea un sitio clonando un repositorio público de GitHub, GitLab, Bitbucket o de tu servidor (o una carpeta local). Se clona en una carpeta temporal y solo al acabar pasa a ser el sitio: si algo falla, no queda nada a medias.
- **Actualizar y cambiar de rama sin sustos:** `roost site pull` solo avanza (fast-forward) y `roost site branch` solo cambia de rama si no hay cambios locales; si los hay, Roost lo dice y no toca nada. `roost site git` enseña la rama, si vas por detrás, el último commit y el historial; con `--adopt`, adopta un proyecto que ya tenía su `.git`.
- **Un git propio y verificado:** Roost descarga MinGit (el git de Git for Windows para aplicaciones) la primera vez que hace falta, comprueba su SHA-256 y lo usa con una configuración aislada: tu `.gitconfig` no se lee ni se toca. `roost git install` y `roost git status`; `[git] exe` en `roost.toml` para usar otro git. De momento, repositorios públicos y locales, y desde la CLI; los privados y la app llegan en las próximas versiones.
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
