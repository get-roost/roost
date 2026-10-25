// Bases de datos: la global (conexión, .env, datos), las instaladas y las disponibles; crear y borrar
// bases de datos, el puerto, instalar y elegir (DatabasesView de la app).
import { h, icon, btn, chip, dot, note, clear, table, shortPath, copy, toast, copyable, segmented } from '../ui.js';
import { onStatus } from '../app.js';

let engineTab = 'mariadb';

export async function render(root, ctx) {
  const { api, dialogs } = ctx;
  const page = h('div.stack');
  root.appendChild(page);
  let d;
  const load = async (catalog = true) => {
    try { d = await api.get('/db' + (catalog ? '?catalog=true' : '')); } catch (e) { clear(page).appendChild(note('bad', e.message)); return; }
    if (ctx.stale()) return;
    draw();
  };
  const act = async (path, body, label) => {
    try { const r = await api.post(path, body); if (r.ok === false) throw new Error(r.error || 'No se pudo'); ctx.flash(label + (r.applied && r.applied.length ? ': ' + r.applied.join('; ') : '')); }
    catch (e) { toast(e.message, 'bad'); }
    await load(false);
  };
  const draw = () => {
    clear(page);
    page.appendChild(h('div.page-head', h('div.titles', h('h1.h1', 'Bases de datos'), h('div.sub', 'MariaDB o MySQL, una en marcha a la vez y solo en este equipo. Root con la contraseña que quieras, y usuarios y bases de datos por proyecto.')),
      h('div.actions', btn('Consola SQL', { onclick: () => toast(d.global ? `Desde la terminal: ${d.global.key === 'mysql' ? 'mysql' : 'mariadb'} --defaults-file="${d.ini}"` : 'No hay base de datos global.') }, icon('terminal', 15)))));
    if (d.linuxNote) page.appendChild(note('info', d.linuxNote));
    if (d.globalProblem) page.appendChild(note('bad', d.globalProblem));
    for (const j of d.jobs || []) page.appendChild(note('info', `${j.title} en segundo plano: ${j.message || '…'}`));

    const main = h('div.stack');
    if (d.global) {
      const port = h('input.input.mono.num', { value: String(d.port), type: 'number', min: '1', max: '65535' });
      const apply = btn('Aplicar', { class: 'primary', disabled: true, onclick: () => act('/db/port', { port: Number(port.value) }, 'Puerto guardado') });
      port.addEventListener('input', () => { apply.disabled = Number(port.value) === d.port; });
      const portNote = d.portState === 'roost' ? note('ok', h('span', h('span.strong', `El ${d.port} lo usa Roost `), `${d.global.label} escucha en 127.0.0.1:${d.port}.`))
        : d.portState === 'free' ? note('info', `El ${d.port} está libre: ${d.global.label} lo usará al iniciar.`)
        : note('bad', `El ${d.port} está ocupado${d.portOwner ? ': ' + d.portOwner : ''}. Pon otro puerto y aplica.`);
      main.appendChild(h('div.card', h('div.card-h', h('span.h2', d.running ? 'En marcha' : 'Parada'), chip(d.global.label, d.running ? 'ok' : '', true),
          h('div.right.row', btn('Reiniciar', { class: 'sm', disabled: !d.running, onclick: () => act('/stack/restart/' + encodeURIComponent(d.service), {}, 'Reiniciada') }), btn(d.running ? 'Parar' : 'Arrancar', { class: 'sm', disabled: !(ctx.state.status && ctx.state.status.daemon && ctx.state.status.daemon.running), onclick: () => act(`/stack/${d.running ? 'stop' : 'start'}/` + encodeURIComponent(d.service || `${d.global.key}-${d.global.branch}`), {}, d.running ? 'Parada' : 'Arrancada') }))),
        h('div.card-b.stack',
          h('div.form-grid', h('div.form-row', h('label', 'Host'), h('div.row', h('div.field.grow', d.host), h('button.btn.icon', { type: 'button', title: 'Copiar', onclick: () => copy(d.host) }, icon('copy', 14)))), h('div.form-row', h('label', 'Puerto'), h('div.row', port, h('button.btn.icon', { type: 'button', title: 'Copiar', onclick: () => copy(String(d.port)) }, icon('copy', 14)), apply)),
            h('div.form-row', h('label', 'Usuario'), h('div.field', d.user)), h('div.form-row', h('label', 'Contraseña'), h('div.row', h('div.field.grow', d.hasPassword ? '••••••••' : '(vacía)'), btn('Cambiar…', { onclick: () => toast('La contraseña de root: desde la terminal, roost db password (próximamente en el panel).') })))),
          portNote,
          h('div', h('div.lbl', 'Para tu .env'), h('div.row', h('div.field.grow', d.env), h('button.btn.icon', { type: 'button', title: 'Copiar', onclick: () => copy(d.env.replace(/  /g, '\n')) }, icon('copy', 14)))),
          h('div.kv', h('div.k', 'Datos'), copyable(d.data), h('div.k', 'Configuración'), h('div.row', h('span.mono.ellipsis', { title: d.ini }, d.ini), h('span.hint', 'my.ini generado')), h('div.k', 'Mis ajustes'), h('div.row', h('span.mono.ellipsis', { title: d.userCnf }, d.userCnf), h('span.hint', 'usuario.cnf: manda sobre lo generado'))))));
      // Bases de datos y usuarios (con el servidor en marcha).
      const name = h('input.input.mono', { placeholder: 'nombre (letras, números y _)' });
      const dbs = h('div.card', h('div.card-h', h('span.h2', 'Bases de datos'), h('span.hint', d.running ? `${d.databases.length} en ${d.global.label}` : 'con la base de datos en marcha'),
          h('div.right.row', name, btn('Crear', { class: 'sm primary', disabled: !d.running, onclick: () => { if (name.value.trim()) act('/db/create', { name: name.value.trim() }, 'Creada ' + name.value.trim()); } }))),
        d.adminProblem ? h('div.card-b', note('bad', d.adminProblem)) : !d.running ? h('div.card-b.muted', 'Arranca el stack para ver y crear bases de datos.') : d.databases.length ? d.databases.map(x => { const tr = h('div.tr', h('span.mono', x), h('div.right', btn('Borrar', { class: 'sm danger', onclick: async () => { if (await dialogs.confirm(`Borrar ${x}`, 'Se borran todas sus tablas. No hay vuelta atrás.', { ok: 'Borrar', danger: true, iconName: 'trash' })) act('/db/drop', { name: x }, 'Borrada ' + x); } }))); tr.style.gridTemplateColumns = '1fr auto'; return tr; }) : h('div.card-b.muted', 'Ninguna todavía.'));
      main.appendChild(dbs);
      if (d.running && d.users.length) main.appendChild(h('div.card', h('div.card-h', h('span.h2', 'Usuarios'), h('span.hint', 'crear y cambiar contraseñas: roost db user (próximamente en el panel)')), d.users.map(u => { const tr = h('div.tr', h('span.mono', `${u.name}@${u.host}`), h('span.hint', u.all ? 'todas las bases de datos' : u.databases.join(', ') || 'sin acceso')); tr.style.gridTemplateColumns = '1fr 1fr'; return tr; })));
    } else {
      main.appendChild(h('div.card', h('div.card-h', h('span.h2', d.none ? 'Sin base de datos' : 'Ninguna instalada')), h('div.card-b.muted', d.none ? 'roost.toml dice [db] engine = "none": el stack va sin base de datos. Elige una instalada para volver a usarla.' : 'Instala una de las disponibles: la primera pasa a ser la global.')));
    }
    main.appendChild(h('div.card', h('div.card-h', h('span.h2', 'Instaladas por Roost'), h('span.hint', 'Una carpeta por versión, los datos aparte por rama')),
      ...table([
        { label: 'Motor', width: '1fr', cell: r => h('span.strong', r.engine) },
        { label: 'Versión', width: '1fr', cell: r => h('span.mono', r.version) },
        { label: 'Soporte', width: '1.5fr', cell: r => chip(r.support, r.tone) },
        { label: 'Datos', width: '1.2fr', cell: r => h('span.mono', r.data) },
        { label: 'Acciones', width: '1.4fr', align: 'right', cell: r => h('div.row.right', r.inUse ? chip('en uso', 'accent') : btn('Usar', { class: 'sm', onclick: () => act('/db/use', { spec: r.spec }, `Global: ${r.label}`) }), btn('Quitar', { class: 'sm danger', disabled: r.inUse && d.running, onclick: async () => { if (await dialogs.confirm(`Quitar ${r.label}`, 'Solo se quitan los binarios. Sus datos (data/) se conservan.', { ok: 'Quitar', danger: true })) act('/db/remove', { spec: r.spec }, `Quitada ${r.label}`); } })) },
      ], d.installed, { empty: 'Ninguna.' }),
      d.installed.length && !d.none ? h('div.card-b', btn('No arrancar ninguna', { class: 'sm', onclick: () => act('/db/use', { spec: 'none' }, 'El stack va sin base de datos') })) : null));

    const side = h('div.stack');
    side.appendChild(h('div.card', h('div.card-h', h('span.h2', 'Abrir con'), h('span.hint', 'Adminer y programas de este equipo')), h('div.card-b.stack', btn('Abrir Adminer', { disabled: true, title: 'Próximamente en el panel' }), h('div.hint', 'Adminer, HeidiSQL, TablePlus y DBeaver se abren desde la app de escritorio. Desde el panel, próximamente.'))));
    const avail = h('div');
    if (d.platform !== 'windows') avail.appendChild(h('div.card-b.muted', 'En Linux, MariaDB y MySQL llegan en la próxima versión.'));
    else if (!d.available) avail.appendChild(h('div.card-b', d.catalogProblem ? note('warn', 'No se pudo leer el catálogo: ' + d.catalogProblem) : h('div.muted', 'Cargando el catálogo…')));
    else {
      avail.appendChild(h('div.card-b', segmented([{ value: 'mariadb', label: 'MariaDB' }, { value: 'mysql', label: 'MySQL' }], engineTab, v => { engineTab = v; draw(); })));
      if (d.catalogProblem) avail.appendChild(h('div.card-b', note('warn', d.catalogProblem)));
      for (const a of d.available.filter(a => a.key === engineTab)) { const tr = h('div.tr', h('div.row', dot(a.tone), h('div', h('div.mono.strong', a.version), h('div.hint', a.support))), h('div.right', a.installed ? chip('instalada', 'ok') : btn('Instalar', { class: 'sm', onclick: () => install(a.spec) }))); tr.style.gridTemplateColumns = '1fr auto'; avail.appendChild(tr); }
    }
    side.appendChild(h('div.card', h('div.card-h', h('span.h2', 'Disponibles')), avail));
    page.appendChild(h('div.grid.main-side', main, side));
  };
  const install = async (spec) => { try { await dialogs.job(() => api.post('/db/install', { spec }), { api, onDone: () => load(false) }); } catch {} await load(false); };
  await load(true);
  const unsub = onStatus(() => { if (ctx.stale()) { unsub(); return; } load(false); });
}
