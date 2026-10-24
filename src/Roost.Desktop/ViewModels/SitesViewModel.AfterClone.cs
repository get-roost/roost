using Roost.Core.Sites;

namespace Roost.Desktop.ViewModels;

public sealed partial class SitesViewModel
{
    /// <summary>
    /// Lo que pasa justo después de clonar un sitio con «Desde git…»: el clonado ya está registrado,
    /// en el hosts y aplicado al stack (con su base de datos, si se pidió), y todavía no se ha
    /// ofrecido la CA ni abierto el navegador. Aquí van los «Pasos tras clonar» (composer install, el
    /// .env desde .env.example, key:generate, migrate, wp-config.php…): el diálogo de
    /// <see cref="OfferDeployStepsAsync"/>, que vuelve al cerrarse y no abre nada si el proyecto no
    /// tiene pasos que proponer.
    /// </summary>
    public Task AfterCloneAsync(Site site) => OfferDeployStepsAsync(site);
}
