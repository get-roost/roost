// Ajustes: general (tema, pool), certificados de confianza (la CA), nombres .test (hosts o DNS local),
// puertos, carpetas y acerca de (SettingsView de la app). Lo de la app de escritorio (bandeja, iniciar
// sesión, programas) no existe en el panel.
import { h, icon, btn, chip, dot, note, clear, copy, toast, copyable, segmented, shortPath, dateText } from '../ui.js';
import { applyTheme, currentTheme } from '../app.js';

export async function render(root, ctx) {
  const { api, dialogs } = ctx;
  const page = h('div.stack');
  root.appendChild(page);
  let s;
  const load = async () => {
    try { s = await api.get('/settings'); } catch (e) { clear(page).appendChild(note('bad', e.message)); return; }
    if (ctx.stale()) return;
    draw();
  };
  const act = async (path, body, label) => {
    try { const r = await api.post(path, body); if (r.ok === false) { if (r.command) await dialogs.confirm('Hace falta permiso de administrador', h('div.col', h('div', r.error), copyable(r.command)), { ok: 'Entendido', iconName: 'warning' }); else throw new Error(r.error || 'No se pudo'); } else ctx.flash(label + (r.applied && r.applied.length ? ': ' + r.applied.join('; ') : '')); }
    catch (e) { toast(e.message, 'bad'); }
    await load();
  };
  const draw = () => {
    clear(page);
    page.appendChild(h('div.page-head', h('div.titles', h('h1.h1', 'Ajustes'), h('div.sub', `${s.configFile}, que también puedes editar a mano.`)), h('div.actions', btn('Copiar la ruta de roost.toml', { onclick: () => copy(s.configFile) }))));
    const left = h('div.stack'), right = h('div.stack');

    // General
    const pool = h('input.input.mono.num', { value: String(s.phpPool), type: 'number', min: '1', max: '32' });
    const soon = (label, hint) => h('label.check', h('input', { type: 'checkbox', disabled: true }), h('div', label, h('div.hint', hint)));
    left.appendChild(h('div.card', h('div.card-h', h('span.h2', 'General')), h('div.card-b.stack',
      soon('Abrir Roost al iniciar sesión (en la bandeja)', 'Es de la app de escritorio.'),
      soon('Iniciar todo al iniciar sesión, sin abrir la app', s.os === 'windows' ? 'Desde la terminal: roost autostart on (una tarea programada de tu usuario).' : 'En Linux llega en la próxima versión.'),
      soon('php en la terminal según la carpeta del proyecto', s.shims.note),
      h('div', h('div.lbl', 'Tema del panel'), segmented([{ value: 'system', label: 'Como el sistema' }, { value: 'light', label: 'Claro' }, { value: 'dark', label: 'Oscuro' }], currentTheme(), v => applyTheme(v)), h('div.hint', 'Solo en este navegador.')),
      h('div', h('div.lbl', s.os === 'windows' ? 'Procesos php-cgi por rama' : 'Tamaño del pool de php-fpm'), h('div.row', pool, btn('Guardar', { class: 'sm', onclick: () => act('/settings/pool', { pool: Number(pool.value) }, 'Pool guardado') }), h('span.hint', 'Cada rama de PHP en uso tiene su pool. Se aplica reiniciando los pools uno a uno.'))),
      h('div.hint', 'Atajos: Ctrl+N nuevo sitio · F5 actualizar la pantalla · Ctrl+L registros · Ctrl+, ajustes'))));

    // Puertos y carpetas
    left.appendChild(h('div.card', h('div.card-h', h('span.h2', 'Puertos y carpetas')), h('div.card-b.stack',
      h('div.kv', h('div.k', 'HTTP / HTTPS'), h('div.row', h('span.mono', `${s.httpPort} / ${s.httpsPort}`), btn('Cambiar', { class: 'link', onclick: () => ctx.navigate('web') })), h('div.k', 'Base de datos'), h('div.row', h('span.mono', String(s.dbPort)), btn('Cambiar', { class: 'link', onclick: () => ctx.navigate('bd') })),
        h('div.k', 'Raíz'), copyable(s.root), h('div.k', 'Proyectos'), copyable(s.www), h('div.k', 'Caché'), copyable(s.cache), h('div.k', 'Registros'), copyable(s.logs), h('div.k', 'git'), h('div.mono', s.gitExe || 'el MinGit de Roost (bin/git)')),
      h('div.hint', 'Cambiar la raíz (moverla) es de la app de escritorio y de [paths] root en roost.toml.'))));

    // Certificados
    const ca = s.ca;
    right.appendChild(h('div.card', h('div.card-h', h('span.h2', 'Certificados de confianza')), h('div.card-b.stack',
      h('div.kv', h('div.k', 'CA local'), h('div', ca.created ? h('div', h('div', ca.subject), h('div.hint', 'caduca ' + dateText(ca.notAfter) + ' · huella ' + (ca.thumbprint || ''))) : h('div', 'Aún no creada: se crea con el primer sitio'), h('div', chip(ca.trusted ? 'el navegador confía en ella' : 'el navegador aún no confía en ella', ca.trusted ? 'ok' : 'warn')), ca.system ? h('div', chip('también el sistema', 'ok')) : null),
        h('div.k', 'Clave'), h('div', h('span.mono', shortPath(ca.keyFile, 40)), h('span.hint', ' · solo la lee tu usuario')), h('div.k', 'Sitios'), h('div', '397 días; se renuevan solos 30 días antes'), h('div.k', 'Dónde'), h('div.hint', ca.store)),
      h('div.row.wrap', btn('Renovar todos', { class: 'sm', onclick: () => act('/ca/renew', {}, 'Certificados renovados') }), btn('Confiar en la CA…', { class: 'sm primary', disabled: ca.trusted, onclick: async () => { await dialogs.trustCa({ api, refresh: load }); } }), btn('Dejar de confiar', { class: 'sm', disabled: !ca.trusted, onclick: async () => { if (await dialogs.confirm('Dejar de confiar en la CA de Roost', 'Se quita de los almacenes de tu usuario. Los sitios https://*.test volverán a dar aviso.', { ok: 'Quitar' })) act('/ca/untrust', {}, 'La CA ya no es de confianza'); } }), s.os === 'windows' ? btn('Configurar Firefox', { class: 'sm', disabled: !ca.trusted || !ca.firefox, title: ca.firefox ? `${ca.firefox} perfil(es)` : 'Sin perfiles de Firefox', onclick: () => act('/ca/trust', { system: false }, 'Firefox configurado') }) : null))));

    // Nombres .test
    const dns = s.dns;
    right.appendChild(h('div.card', h('div.card-h', h('span.h2', 'Nombres .test'), h('span.hint', 'Cómo se resuelven')), h('div.card-b.stack',
      h('label.radio', h('input', { type: 'radio', name: 'resolver', checked: s.resolver === 'hosts', onchange: () => act('/settings/resolver', { resolver: 'hosts' }, 'Los .test van por el hosts') }), h('div', h('div.strong', 'Fichero hosts del sistema'), h('div.hint', `${s.os === 'windows' ? 'Pide permiso de administrador (UAC)' : 'Pide permiso de administrador (pkexec o sudo)'} al añadir o quitar un sitio. Lo hace roost-helper, que solo toca su bloque. ${s.hosts.file}${s.hosts.system ? '' : ' (de pruebas, ROOST_HOSTS_FILE)'}`))),
      h('label.radio', h('input', { type: 'radio', name: 'resolver', checked: s.resolver === 'dns', disabled: !dns.available, onchange: () => act('/settings/resolver', { resolver: 'dns' }, 'Los .test van por el DNS local') }), h('div', h('div.strong', 'DNS local, sin permisos al añadir sitios'), h('div.hint', dns.available ? `Un solo permiso, al ponerlo: ${s.os === 'windows' ? 'una regla de Windows manda los .test al DNS de Roost' : 'un drop-in de systemd-resolved manda los .test al DNS de Roost'}, que los responde mientras el stack está en marcha.` : dns.whyNot))),
      dns.available ? h('div.row', dot(dns.running ? 'ok' : ''), h('span.hint', dns.ruleInstalled && s.resolver !== 'dns' ? `${dns.ruleLabel} sigue puesta, pero Roost usa el hosts.` : dns.running ? `El DNS local responde en ${dns.endPoint}.` : `El DNS local no está en marcha${s.resolver === 'dns' ? ' (arranca con el stack)' : ' (no hace falta: los .test van por el hosts)'}.`)) : null,
      h('div.row.wrap', btn('Ver el bloque del hosts', { class: 'sm', onclick: async () => { try { const b = await api.get('/sites/hosts-block'); dialogs.text('Bloque de Roost en el hosts', b.file, b.block || '(sin sitios)'); } catch (e) { toast(e.message, 'bad'); } } }), dns.available ? btn('¿Contesta el DNS local?', { class: 'sm', onclick: async () => { try { const r = await api.post('/settings/dns-check'); toast(r.answers ? `Sí: ${r.endPoint} responde.` : `No: ${r.endPoint} no contesta.`, r.answers ? '' : 'bad'); } catch (e) { toast(e.message, 'bad'); } } }) : null, btn('Registro del helper', { class: 'sm link', onclick: () => ctx.navigate('registros', { file: 'helper' }) })),
      s.hosts.helperExists ? null : note('warn', `No se encuentra roost-helper (${s.hosts.helper}): sin él, el hosts y la CA del sistema hay que ponerlos a mano.`))));

    // Acerca de
    right.appendChild(h('div.card', h('div.card-h', h('span.h2', 'Acerca de')), h('div.card-b.stack',
      h('div.kv', h('div.k', 'Roost'), h('div', s.version + ' · panel web'), h('div.k', 'Equipo'), h('div', `${s.user}@${s.machine} (${s.os === 'windows' ? 'Windows' : 'Linux'})`), h('div.k', 'Panel'), h('div', h('span.mono', shortPath(s.panel.log, 44)), h('span.hint', ` · pid ${s.panel.pid}`)), h('div.k', 'Actualizaciones'), h('div.hint', s.updates.env ? 'Las comprobaciones automáticas están apagadas (ROOST_NO_UPDATE_CHECK).' : 'Desde la terminal: roost update.')),
      h('div.row', btn('Novedades', { class: 'sm', onclick: () => ctx.navigate('novedades') })))));

    page.appendChild(h('div.grid.two', left, right));
  };
  await load();
}
