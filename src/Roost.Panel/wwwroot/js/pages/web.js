// Servidor web: el activo (nginx o Apache), los puertos comprobados en vivo, lo instalado y sus
// ficheros, Validar y Recargar (WebServerView de la app).
import { h, icon, btn, chip, dot, note, clear, shortPath, copy, toast, copyable, segmented } from '../ui.js';
import { onStatus } from '../app.js';

let tab = null;

export async function render(root, ctx) {
  const { api, dialogs } = ctx;
  const page = h('div.stack');
  root.appendChild(page);
  let w;
  const load = async () => {
    try { w = await api.get('/web'); } catch (e) { clear(page).appendChild(note('bad', e.message)); return; }
    if (ctx.stale()) return;
    draw();
  };
  const draw = () => {
    clear(page);
    if (!tab) tab = w.server;
    page.appendChild(h('div.page-head', h('div.titles', h('h1.h1', 'Servidor web'), h('div.sub', 'Un solo servidor atiende localhost y los sitios .test. Puedes tener los dos instalados y elegir cuál arranca.'))));
    for (const j of w.jobs || []) page.appendChild(note('info', `${j.title} en segundo plano: ${j.message || '…'}`));

    const http = h('input.input.mono.num', { value: String(w.http.port), type: 'number', min: '1', max: '65535' });
    const https = h('input.input.mono.num', { value: String(w.https.port), type: 'number', min: '1', max: '65535' });
    const apply = btn('Aplicar', { class: 'primary', disabled: true, onclick: async (e) => {
      e.target.disabled = true;
      try { const r = await api.post('/web/ports', { http: Number(http.value), https: Number(https.value) }); if (!r.ok) throw new Error(r.error); ctx.flash('Puertos guardados' + (r.applied && r.applied.length ? ': ' + r.applied.join('; ') : '')); }
      catch (err) { toast(err.message, 'bad'); }
      await load();
    } });
    const check = () => { apply.disabled = Number(http.value) === w.http.port && Number(https.value) === w.https.port; };
    http.addEventListener('input', check); https.addEventListener('input', check);
    const portState = (p) => chip(p.text, p.tone);
    const radio = (server, title, tag, tagTone, text, disabled) => h('label.radio', h('input', { type: 'radio', name: 'server', checked: w.server === server, disabled, onchange: () => switchServer(server) }), h('div', h('div.row', h('span.strong', title), chip(tag, tagTone)), h('div.hint', text)));
    const active = h('div.card', h('div.card-h', h('span.h2', 'Servidor activo'), h('span.hint', 'Cambiarlo no para PHP ni la base de datos')),
      h('div.card-b', h('div.grid.main-side', h('div.col', h('div.lbl', 'Arranca con Iniciar todo'),
        radio('nginx', `nginx ${w.nginx.version}`, w.nginx.installed ? (w.server === 'nginx' && w.running ? 'en marcha' : 'instalado') : 'se instala al elegirlo', w.nginx.installed && w.server === 'nginx' && w.running ? 'ok' : '', 'Rápido y ligero. PHP NTS a través de los pools de php-cgi' + (ctx.state.status && ctx.state.status.os === 'linux' ? ' (php-fpm en Linux)' : '') + '.'),
        radio('apache', `Apache ${w.apache.version}`, !w.apache.available ? 'no está en el sistema' : w.apache.installed ? (w.server === 'apache' && w.running ? 'en marcha' : w.apache.distro ? 'de la distro' : 'instalado') : 'se instala al elegirlo', w.apache.installed && w.server === 'apache' && w.running ? 'ok' : '', w.apache.why || 'Lee los .htaccess (WordPress, Laravel…). Los mismos PHP, y cada sitio con el suyo.' + (w.apache.distro ? ' Solo puertos desde el 1024.' : ''), !w.apache.available)),
        h('div.row', h('div.form-row', h('label', 'Puerto HTTP'), http, portState(w.http)), h('div.form-row', h('label', 'Puerto HTTPS'), https, portState(w.https)), h('div.form-row', h('label', ' '), apply)))));
    page.appendChild(active);

    page.appendChild(segmented([{ value: 'nginx', label: 'nginx' }, { value: 'apache', label: 'Apache 2' }], tab, v => { tab = v; draw(); }));
    const s = tab === 'apache' ? w.apache : w.nginx;
    const conf = (file, text, action) => { const tr = h('div.tr', h('div.row', h('span.mono', shortPath(file, 34)), h('span.hint', text)), h('div.right', action)); tr.style.gridTemplateColumns = '1fr auto'; return tr; };
    const installed = h('div.card', h('div.card-h', h('span.h2', s.installed ? 'Instalado' : 'No instalado')),
      s.installed ? h('div.card-b.stack',
        h('div.row', h('span.ver', s.version), chip(tab === 'nginx' ? 'rama estable' : w.apache.distro ? 'de la distro' : 'Apache Lounge', ''), chip(tab === 'apache' && w.apache.distro ? 'paquete del sistema (apt)' : w.nginx.verified || 'SHA-256 verificado', '')),
        h('div.kv', h('div.k', 'Carpeta'), copyable(s.directory), h('div.k', 'Escucha en'), h('div.mono', w.nginx.listen), h('div.k', 'Módulos'), h('div.hint', tab === 'nginx' ? 'http_ssl, http_v2' : 'mod_proxy_fcgi, mod_rewrite, mod_ssl, mod_headers…')),
        h('div.lbl', 'Configuración'),
        h('div.card', conf(s.conf, 'generado: no se edita', btn('Ver', { class: 'link', onclick: async () => { try { const f = await api.get('/web/file?name=' + (tab === 'apache' ? 'httpd.conf' : 'nginx.conf')); dialogs.text(tab === 'apache' ? 'httpd.conf' : 'nginx.conf', f.file, f.text || '(aún no generado: lo escribe el primer Iniciar todo)'); } catch (e) { toast(e.message, 'bad'); } } })),
          conf(s.sitesDir, 'uno por sitio: editable fuera de las marcas', btn('Sitios', { class: 'link', onclick: () => ctx.navigate('sitios') })),
          conf(s.errorLog, 'errores y avisos', btn('Abrir', { class: 'link', onclick: () => ctx.navigate('registros', { file: (tab === 'apache' ? 'apache' : 'nginx') + '-error.log' }) }))),
        h('div.row', btn('Validar', { class: 'sm', disabled: w.server !== tab, onclick: async () => { try { const r = await api.post('/web/validate'); dialogs.message(r.ok ? 'Configuración válida' : 'La configuración no vale', r.output, r.ok ? 'ok' : 'bad'); } catch (e) { toast(e.message, 'bad'); } } }), btn('Recargar', { class: 'sm', disabled: !w.running || w.server !== tab, onclick: async () => { try { const r = await api.post('/web/reload'); if (!r.ok) throw new Error(r.error); ctx.flash('Recargado: ' + (r.applied || []).join('; ')); } catch (e) { toast(e.message, 'bad'); } } }), h('span.hint', 'Validar es el mismo -t que hace Iniciar todo; Recargar relee la configuración en caliente.')))
      : h('div.card-b', h('div.muted', s.why || `Se instala solo al elegirlo como servidor activo (${s.version}, ${tab === 'nginx' ? w.nginx.source : 'Apache Lounge'}).`)));
    const available = h('div.card', h('div.card-h', h('span.h2', 'Disponibles'), h('span.hint', tab === 'nginx' ? w.nginx.source : w.apache.distro ? 'la distro' : 'apachelounge.com')),
      (() => { const tr = h('div.tr', h('div.row', dot('ok'), h('div', h('div.mono.strong', s.version), h('div.hint', tab === 'nginx' ? 'estable: la recomendada' : w.apache.distro ? 'el apache2 de la distro' : 'Win64, VS18'))), h('div.right', s.installed ? chip('instalada', 'ok') : chip(s.why ? 'no está en el sistema' : 'al elegirlo', ''))); tr.style.gridTemplateColumns = '1fr auto'; return tr; })(),
      h('div.card-b', note('info', tab === 'nginx' ? (w.nginx.source === 'nginx.org' ? 'nginx.org no publica checksums: Roost solo instala versiones cuya firma PGP se ha verificado, y cada versión nueva llega con una actualización de Roost.' : 'El tar.gz estático de get-roost/roost-builds, con su SHA-256 fijado en Roost y su attestation.') : (w.apache.note || `Apache necesita el ${w.apache.redistributable}; el instalador de Roost lo pone si falta.`))));
    page.appendChild(h('div.grid.main-side', installed, available));
  };

  const switchServer = async (server) => {
    try { await dialogs.job(() => api.post('/web/server', { server }), { api }); ctx.flash(`Servidor activo: ${server}`); }
    catch {}
    tab = server;
    await load();
  };

  await load();
  const unsub = onStatus(() => { if (ctx.stale()) { unsub(); return; } load(); });
}
