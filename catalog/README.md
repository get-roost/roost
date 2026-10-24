# catalog/: lo escrito a mano del feed de avisos

El feed firmado de avisos (`roost-catalog`) se genera cada día con
`.github/workflows/catalog.yml` y se publica en la rama `catalog` de este repo. Casi todo es
automático (endoflife.date, php.net, los avisos de GitHub de `php/php-src`, los boletines de
nginx.org y de httpd.apache.org y el catálogo KEV de CISA); esta carpeta es el resto:

- `catalog.json`: los productos que lleva el feed y los textos de remediación.
- `advisories/<id>.json`: un fichero por aviso que necesita una persona.

## Un aviso a mano

Si el `id` (o un alias) es el de un aviso que ya trae una fuente, los campos que pongas sustituyen
a los suyos; los que no pongas, se quedan como vienen. Si no lo trae ninguna fuente, es un aviso
nuevo y necesita `product` y `affected`.

```json
{
  "id": "CVE-2026-109619",
  "note": "Para quien lo lea: por qué existe este fichero (no va al feed).",
  "aliases": ["PHP2Shell"],
  "title": "PHP2Shell: RCE sin autenticacion via session.upload_progress",
  "affected": [">=8.2.0 <8.2.35", ">=8.3.0 <8.3.36", "<8.2.0"],
  "fixedIn": { "8.2": "8.2.35", "8.3": "8.3.36" },
  "cvss": 9.2,
  "mitigations": [{ "ini": "session.upload_progress.enabled", "value": "0", "note": "y reinicia los workers" }],
  "refs": ["https://www.php.net/releases/"]
}
```

- **Rangos:** restricciones separadas por espacios que se cumplen todas (`>=8.3.0 <8.3.36`); el
  aviso aplica si casa alguno. Versiones siempre X.Y.Z. Un rango que no se lee para el feed.
- **Mitigaciones:** `ini` es la directiva y `value`, el valor que mitiga, tal como se escribe en un
  `php.ini`. `compare` dice cómo se compara con lo que aplica PHP (`php -i`): `"="` (por defecto;
  On/1/true son lo mismo, y los tamaños se comparan en bytes), `"<="` o `">="` (un tamaño o un
  número como mucho o como poco, como `memory_limit <= 128M`) o `"includes"` (una lista como
  `disable_functions` que tiene que llevarlos todos). Roost lo enseña como «mitigada / sin mitigar»
  y, en sus PHP, la puede aplicar en `conf.d/<rama>/50-roost-mitigaciones.ini`: un valor con comillas,
  `;`, `$` o llaves no se publica.
- **`kev`** no se escribe: sale del catálogo de CISA.
- **`"ignore": true`** quita del feed un aviso automático que está mal (explica por qué en `note`).

## Retenidos

Un aviso automático que dejaría en rojo **la última versión** de una rama con soporte no se
publica solo: o aún no hay parche o se ha leído mal. Sale en el resumen del run como «Retenido».
Para publicarlo, mira el aviso y crea su fichero aquí (basta con `id` y `note`; corrige `affected`
si hace falta).

## Probarlo en local

```sh
dotnet run --project src/Roost.Catalog -- generate --catalog catalog --out <dir> --unsigned
```
