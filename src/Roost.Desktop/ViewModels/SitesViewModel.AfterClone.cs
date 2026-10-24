using Roost.Core.Sites;

namespace Roost.Desktop.ViewModels;

public sealed partial class SitesViewModel
{
    /// <summary>
    /// Lo que pasa justo después de clonar un sitio con «Desde git…»: el clonado ya está registrado,
    /// en el hosts y aplicado al stack (con su base de datos, si se pidió), y todavía no se ha
    /// ofrecido la CA ni abierto el navegador.
    /// <para>
    /// Aquí van los «Pasos tras clonar» (composer install, el .env desde .env.example, key:generate,
    /// migrate, wp-config.php…), que son del paquete «Pasos y plantillas» y se hacen a la vez que
    /// este: el integrador los engancha en este método al juntar las dos ramas. Mientras, no hace nada.
    /// </para>
    /// </summary>
    public Task AfterCloneAsync(Site site) => Task.CompletedTask;
}
