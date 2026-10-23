# Marca de Roost

Logo e iconos separados a partir del concepto aprobado del ave con forma de R.
Los archivos finales PNG y el ICO tienen transparencia real (canal alfa).

## Archivos

| Ruta | Contenido |
| --- | --- |
| `logo/roost-logo.png` | Logo horizontal: ave azul y nombre en grafito; 2172 × 724 px. |
| `icons/roost-icon.png` | Ave sin texto ni recuadro; maestro de 1254 × 1254 px. |
| `icons/png/roost-*.png` | Iconos de 16, 24, 32, 48, 64, 128, 256 y 512 px. |
| `icons/roost.ico` | Icono de Windows con resoluciones de 16 a 256 px. |
| `source/roost-approved-concept.png` | Lámina original aprobada; solo referencia, con fondo blanco. |
| `source/roost-*-prompt.txt` | Prompts exactos utilizados para separar el logo y el símbolo. |

Los dos maestros se generaron con la herramienta integrada imagegen, usando el
concepto aprobado como referencia. Son imágenes raster PNG, no vectores SVG.
El logo con texto en grafito está pensado para superficies claras.

## Volver a exportar los iconos

Desde la raíz del repositorio, en Windows con PowerShell:

```powershell
pwsh -NoProfile -File .\assets\branding\export-icons.ps1
```

También puede ejecutarse con Windows PowerShell (`powershell` en lugar de `pwsh`).
El script conserva el maestro, mantiene el canal alfa y regenera los tamaños PNG
y el ICO. No requiere paquetes adicionales.
