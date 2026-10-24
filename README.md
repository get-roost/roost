# roost-catalog

El feed de avisos de seguridad y ciclos de vida que usa Roost para `roost audit`
y la pantalla Auditoría (ROOST.md §7.3). Lo regenera cada día el workflow
`catalog.yml` de la rama `main` a partir de endoflife.date, php.net, los avisos
de GitHub de `php/php-src`, los de nginx.org y httpd.apache.org y el catálogo KEV
de CISA, más los avisos revisados a mano de `catalog/advisories/`.

- `advisories.json`: el feed.
- `advisories.json.sig`: su firma, ECDSA P-256 sobre SHA-256 (DER).
- `roost-catalog.pub.pem`: la clave pública (Roost la lleva dentro).

Comprobarlo sin Roost:

    openssl dgst -sha256 -verify roost-catalog.pub.pem -signature advisories.json.sig advisories.json

No edites esta rama a mano: se sobrescribe cada día.
