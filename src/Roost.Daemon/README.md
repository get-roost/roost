# Roost.Daemon

`roostd`, el daemon de [Roost](https://github.com/get-roost/roost): el proceso de usuario que mantiene el stack local (base de datos, pools de php-cgi, nginx y los servicios de las extensiones) y lo expone por HTTP+JSON en una named pipe (Windows) o un unix socket, nunca por TCP. Trae también su cliente (`DaemonClient`) y su lanzador (`DaemonLauncher`).

No hace falta para usar Roost: este paquete es para compilar extensiones o herramientas que hablen con el daemon. Depende de [Roost.Core](https://www.nuget.org/packages/Roost.Core), de [Roost.Extensibility](https://www.nuget.org/packages/Roost.Extensibility) y del framework compartido de ASP.NET Core (`Microsoft.AspNetCore.App`).

La API todavía no es estable: mientras la versión sea 0.x, cualquier versión menor puede romperla.

Licencia MIT.
