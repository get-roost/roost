// Sitios: la lista, el panel del sitio y los diálogos «Nuevo sitio» y quitar (SitesView de la app).
import { h, icon, btn, chip, dot, note, clear, table, shortPath, copy, toast, copyable, segmented, plural } from '../ui.js';
import { onStatus } from '../app.js';

let selectedName = null;

export async function render(root, ctx) {
  const { api, navigate, dialogs } = ctx;
  if (ctx.params.site) selectedName = ctx.params.site;
  const page = h('div.stack');
  root.appendChild(page);
  let data;
  const load = async () => {
    try { data = await api.get('/sites'); } catch (e) { clear(page).appendChild(note('bad', e.message)); return; }
    if (ctx.stale()) return;
    draw();
  };
  let filter = '';
  const draw = () => {
    clear(page);
    const sites = data.sites;
    if (selectedName && !sites.some(s => s.name === selectedName)) selectedName = null;
    if (!selectedName && sites.length) selectedName = sites[0].name;
    page.appendChild(h('div.page-head',
      h('div.titles', h('h1.h1', 'Sitios'), h('div.sub', 'Cada sitio se sirve en https://<nombre>.test con su certificado. Puede usar el PHP global o uno propio.')),
      h('div.actions',
        btn('Aparcar carpeta…', { onclick: () => toast('Aparcar carpetas: desde la terminal, roost park <carpeta> (próximamente en el panel).') }, icon('folder', 15)),
        btn('Desde git…', { onclick: () => toast('Clonar desde git: desde la terminal, roost site clone <url> (próximamente en el panel).') }, icon('gitBranch', 15)),
        btn('Nuevo sitio', { class: 'primary', onclick: () => newSite(ctx, load) }, icon('plus', 15)))));

    if (data.hosts.mode === 'hosts' && !data.hosts.inSync && data.hosts.missing && data.hosts.missing.length)
      page.appendChild(note('warn', h('div.grow', `Falta${data.hosts.missing.length === 1 ? '' : 'n'} en el hosts ${data.hosts.missing.join(', ')}: no resolverá${data.hosts.missing.length === 1 ? '' : 'n'} hasta que se añada${data.hosts.missing.length === 1 ? '' : 'n'}.`), btn('Añadir al hosts', { class: 'sm', onclick: () => syncHosts(ctx, load) })));
    for (const j of ctx.state.status && ctx.state.status.jobs || []) page.appendChild(note('info', `${j.title} en segundo plano: ${j.message || '…'}`));

    const shown = sites.filter(s => !filter || s.name.includes(filter) || s.folder.toLowerCase().includes(filter));
    const list = h('div.card',
      h('div.card-h', h('span.h2', plural(sites.length, 'sitio', 'sitios')), h('div.right.row', h('span.lbl', 'Buscar'), h('input.input', { placeholder: 'nombre o carpeta', value: filter, oninput: (e) => { filter = e.target.value.toLowerCase(); draw(); } }))),
      ...table([
        { label: 'Sitio', width: '1.6fr', cell: s => h('div', h('div.mono.strong', s.host), h('div.hint.ellipsis', { title: s.folder }, shortPath(s.folder, 34))) },
        { label: 'PHP', width: '1fr', cell: s => chip(s.phpLabel, s.phpEol ? 'bad' : '') },
        { label: 'Certificado', width: '1.2fr', cell: s => chip(s.certEmitted ? 'emitido' : 'se emite al iniciar', s.certEmitted ? 'ok' : '') },
        { label: 'Git', width: '.5fr', cell: s => s.git ? chip(s.git.branch, 'accent') : chip('—') },
        { label: 'hosts', width: '.5fr', cell: s => chip(s.inHosts ? 'sí' : 'no', s.inHosts ? 'ok' : 'warn') },
      ], shown, { onRow: s => { selectedName = s.name; draw(); }, selected: s => s.name === selectedName, empty: sites.length ? 'Ningún sitio con ese nombre.' : 'Ningún sitio todavía. Nuevo sitio crea uno en https://<nombre>.test.' }));

    const site = sites.find(s => s.name === selectedName);
    page.appendChild(h('div.grid.main-side', list, site ? panel(site) : h('div')));
  };

  const panel = (s) => {
    const running = ctx.state.status && ctx.state.status.daemon && ctx.state.status.daemon.running;
    const body = h('div.card-b.stack');
    if (!s.inHosts && data.hosts.mode === 'hosts') body.appendChild(note('warn', h('div.grow', `Falta en el hosts: no resolverá hasta que se añada (${data.elevationNote})`, h('div', btn('Añadir al hosts', { class: 'sm', onclick: () => syncHosts(ctx, load) })))));
    if (!s.folderExists) body.appendChild(note('warn', `La carpeta del proyecto ya no existe (${s.folder}). El sitio dará error hasta que vuelva; si ya no lo quieres, quítalo.`));
    else if (!s.rootExists) body.appendChild(note('warn', `La raíz web ya no existe (${s.documentRoot}). Cámbiala o el sitio dará error.`));
    if (s.phpProblem) body.appendChild(note('bad', s.phpProblem));
    body.appendChild(h('div.kv',
      h('div.k', 'Direcciones'), h('div.row', h('div', h('div.mono', h('a', { href: s.https, target: '_blank', rel: 'noreferrer' }, s.https)), h('div.mono', h('a', { href: s.http, target: '_blank', rel: 'noreferrer' }, s.http))), h('button.btn.sm.icon.ghost.right', { type: 'button', title: 'Copiar', onclick: () => copy(s.https) }, icon('copy', 14))),
      h('div.k', 'Alias'), h('div', h('div.hint', s.aliases.length ? s.aliases.join(', ') : `Ninguno; ya responde por cualquier *.${s.host}.`), btn('Alias…', { class: 'sm', onclick: () => toast('Alias: desde la terminal, roost site alias ' + s.name + ' add <nombre>.test (próximamente en el panel).') })),
      h('div.k', 'Carpeta'), copyable(s.folder),
      h('div.k', 'Raíz web'), h('div', h('div.mono', s.root || '(la carpeta)'), h('div.row.wrap', btn('Cambiar…', { class: 'sm', onclick: () => changeRoot(s) }), s.typicalRoots.filter(r => r !== s.root).map(r => h('button.btn.chip-btn', { type: 'button', onclick: () => setRoot(s, r) }, r + '/')), s.root ? h('button.btn.chip-btn', { type: 'button', onclick: () => setRoot(s, '') }, 'la carpeta') : null)),
    ));
    const phpSelect = h('select.input', h('option', { value: '' }, `Global (${data.globalPhp ? data.globalPhp.branch : '—'})`), data.phpOptions.map(p => h('option', { value: p.name, selected: s.php === p.name }, p.label)));
    phpSelect.addEventListener('change', async () => {
      const value = phpSelect.value;
      const ok = await dialogs.confirm('Cambiar el PHP del sitio', `${s.host} pasará a ${value ? phpSelect.selectedOptions[0].textContent : 'el PHP global'}.${running ? ' Roost arranca el PHP de esa rama antes de mandarle nada; cada rama en uso tiene su pool.' : ''}`, { ok: 'Cambiar', iconName: 'code' });
      if (!ok) { phpSelect.value = s.php || ''; return; }
      try { const r = await api.post(`/sites/${s.name}/php`, { php: value || null }); if (!r.ok) throw new Error(r.error); ctx.flash('PHP del sitio cambiado'); }
      catch (e) { toast(e.message, 'bad'); }
      await load();
    });
    body.appendChild(h('div', h('div.lbl', 'PHP del sitio'), phpSelect, h('div.hint', 'Se aplica sin cortar: Roost arranca el PHP de esa rama antes de mandarle nada. Cada rama en uso tiene su pool.')));
    if (s.project) body.appendChild(h('div', h('div.lbl', 'Proyecto'), h('div.row.wrap', chip(s.project.type === 'Plain' ? 'Carpeta' : s.project.type, 'accent'), s.project.phpConstraint ? h('span.hint', 'composer.json pide PHP ' + s.project.phpConstraint) : null, s.project.composer && !s.project.vendor ? h('span.hint', 'falta vendor/: roost site composer ' + s.name + ' install') : null)));
    body.appendChild(h('div', h('div.lbl', 'Base de datos'), h('div.row', h('span.mono', s.database || s.databaseGuess), s.database ? null : h('span.hint', '(parece de este sitio)'), h('div.right', btn('Ir a Bases de datos', { class: 'sm', onclick: () => navigate('bd') })))));
    if (s.git) body.appendChild(h('div', h('div.lbl', 'Git'), h('div.hint', `${s.git.remote} · rama ${s.git.branch}. Actualizar, cambiar de rama y el historial: desde la terminal (roost site pull/branch/git ${s.name}); próximamente en el panel.`)));
    body.appendChild(h('div.row.wrap', btn('Ver vhost', { class: 'sm', onclick: async () => { try { const c = await api.get(`/sites/${s.name}/conf`); dialogs.text(c.file.split(/[\\/]/).pop(), c.file, c.text || '(aún no generado: lo escribe el primer Iniciar todo)'); } catch (e) { toast(e.message, 'bad'); } } }), btn('Registros', { class: 'sm', onclick: () => navigate('registros', { site: s.name }) }), btn('Quitar…', { class: 'sm danger right', onclick: () => removeSite(s) })));
    return h('div.card', h('div.card-h', h('span.mono.strong', s.host), h('div.right.row', btn('Editar…', { class: 'sm', onclick: () => toast('Renombrar o mover: desde la terminal, roost site rename/move (próximamente en el panel).') }), btn('Abrir', { class: 'sm', onclick: () => window.open(s.https, '_blank') }))), body);
  };

  const setRoot = async (s, root) => {
    try { const r = await api.post(`/sites/${s.name}/root`, { root }); if (!r.ok) throw new Error(r.error); ctx.flash('Raíz web cambiada'); }
    catch (e) { toast(e.message, 'bad'); }
    await load();
  };
  const changeRoot = async (s) => {
    const input = h('input.input.mono', { value: s.root || '', placeholder: 'public, web, dist… (vacío: la carpeta)' });
    const ok = await dialogs.confirm('Raíz web de ' + s.host, h('div.col', h('div', 'La subcarpeta del proyecto que se sirve, relativa a ', h('span.mono', s.folder), '.'), input), { ok: 'Cambiar', iconName: 'folder' });
    if (ok) await setRoot(s, input.value.trim());
  };
  const removeSite = async (s) => {
    const ok = await dialogs.confirm(`Quitar ${s.host}`, h('div', 'Se quitan el vhost, el certificado y la entrada del hosts. ', h('span.strong', 'La carpeta del proyecto no se toca.'), s.git ? ' Su clave SSH y su token se quedan (roost site remove los quita).' : ''), { ok: 'Quitar', danger: true, iconName: 'trash' });
    if (!ok) return;
    try { const r = await api.del(`/sites/${s.name}`); ctx.flash(`Quitado ${s.host}`); if (r.hosts && !r.hosts.ok) toast(r.hosts.message, 'bad'); }
    catch (e) { toast(e.message, 'bad'); }
    await load();
  };

  await load();
  const unsub = onStatus(() => { if (ctx.stale()) { unsub(); return; } load(); });
}

export async function syncHosts(ctx, reload) {
  try {
    const r = await ctx.api.post('/sites/hosts');
    if (r.hosts.ok) ctx.flash(r.hosts.message);
    else await hostsProblem(ctx, r.hosts);
  } catch (e) { toast(e.message, 'bad'); }
  if (reload) await reload();
}

async function hostsProblem(ctx, hosts) {
  await ctx.dialogs.confirm('No se pudo actualizar el hosts', h('div.col', h('div', hosts.message), hosts.command ? h('div', h('div.lbl', 'Hazlo a mano con permiso de administrador'), copyable(hosts.command)) : null, hosts.block ? h('div', h('div.lbl', 'O añade este bloque a ' + hosts.file), h('pre.log', hosts.block)) : null), { ok: 'Entendido', cancel: 'Cerrar', iconName: 'warning' });
}

/** «Nuevo sitio»: nombre, carpeta, raíz web, PHP y base de datos; «Crear y abrir» registra, hosts, aplica y abre. */
export async function newSite(ctx, reload) {
  const { api, dialogs } = ctx;
  let options;
  try { options = await api.get('/sites'); } catch (e) { await dialogs.message('Nuevo sitio', e.message, 'bad'); return; }
  const name = h('input.input.mono', { placeholder: 'tienda', autocomplete: 'off', spellcheck: 'false' });
  const folder = h('input.input.mono', { placeholder: options.www + (options.www.includes('\\') ? '\\' : '/') + '<nombre> (vacío: ahí)', autocomplete: 'off', spellcheck: 'false' });
  const rootIn = h('input.input.mono', { placeholder: 'public (opcional)', autocomplete: 'off' });
  const nameHint = h('div.hint'), folderHint = h('div.hint'), rootHint = h('div.hint', 'La subcarpeta que se sirve; el resto del proyecto queda fuera de la web.');
  const php = h('select.input', h('option', { value: '' }, `Global (${options.globalPhp ? options.globalPhp.branch : '—'})`), options.phpOptions.map(p => h('option', { value: p.name }, p.label)));
  const phpHint = h('div.hint');
  let dbMode = 'create';
  const dbName = h('input.input.mono', { placeholder: 'tienda' });
  const dbSeg = segmented([{ value: 'create', label: 'Crear una' }, { value: 'existing', label: 'Usar una que existe' }, { value: 'none', label: 'Ninguna' }], dbMode, v => { dbMode = v; dbName.classList.toggle('hidden', v === 'none'); dbHint.textContent = v === 'create' ? 'Se crea en la base de datos global al terminar (si está en marcha).' : v === 'existing' ? 'Se apunta como la base de datos del sitio.' : ''; });
  const dbHint = h('div.hint', 'Se crea en la base de datos global al terminar (si está en marcha).');
  const summary = h('ul.muted');
  const errorNote = h('div.hidden');
  let preview = null, timer = null;
  const update = async () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      try { preview = await api.get(`/sites/preview?name=${encodeURIComponent(name.value)}&folder=${encodeURIComponent(folder.value)}&root=${encodeURIComponent(rootIn.value)}`); }
      catch { return; }
      nameHint.textContent = preview.nameHint; name.classList.toggle('bad', name.value.length > 0 && !preview.validName);
      folderHint.textContent = preview.folderHint;
      if (preview.suggestedRoot && !rootIn.value) { rootIn.placeholder = preview.suggestedRoot + ' (detectada)'; }
      if (preview.recommended && !php.value) { php.value = preview.recommended; phpHint.textContent = preview.recommendedWhy; } else phpHint.textContent = preview.phpConstraint ? 'composer.json pide PHP ' + preview.phpConstraint : '';
      if (!dbName.value && preview.validName) dbName.value = preview.name.replace(/[^a-z0-9_]/g, '_');
      clear(summary);
      if (preview.validName) {
        summary.append(h('li', `Usa ${preview.folder}${rootIn.value || preview.suggestedRoot ? ` (raíz web: ${rootIn.value || preview.suggestedRoot}/)` : ''}${preview.exists ? '' : ' (la crea con un index.php)'}`),
          h('li', `Escribe el vhost del servidor web y emite el certificado de ${preview.name}.test y *.${preview.name}.test`),
          h('li', `Añade ${preview.name}.test al hosts: ${preview.hostsNote}`),
          dbMode === 'create' ? h('li', `Crea la base de datos ${dbName.value || preview.name} (si la global está en marcha)`) : null);
      }
    }, 250);
  };
  for (const el of [name, folder, rootIn]) el.addEventListener('input', update);
  const create = async (e) => {
    const button = e.target;
    if (!name.value.trim()) { name.focus(); return; }
    button.disabled = true;
    errorNote.classList.add('hidden');
    try {
      const r = await api.post('/sites', { name: name.value.trim(), folder: folder.value.trim() || null, root: rootIn.value.trim() || null, php: php.value || null, database: dbMode === 'none' ? null : (dbName.value.trim() || null), createDatabase: dbMode === 'create' });
      d.close(true);
      ctx.flash(`${r.site.created ? 'Creado' : 'Actualizado'} ${r.site.host}`);
      if (r.hosts && !r.hosts.ok) await hostsProblem(ctx, r.hosts);
      if (r.applied && !r.applied.ok) await dialogs.message('No se pudo aplicar al stack', r.applied.error || '', 'bad');
      if (r.database && !r.database.ok) toast(r.database.message, 'bad');
      if (reload) await reload();
      if (!r.caTrusted) { const trusted = await dialogs.trustCa({ api, refresh: () => {} }); if (trusted !== null) {} }
      const running = r.applied && r.applied.running;
      if (running) window.open(r.url, '_blank');
      else { const start = await dialogs.confirm('Sitio listo', `${r.site.host} queda registrado. El stack está parado: ¿lo arrancamos ahora?`, { ok: 'Iniciar todo', iconName: 'play' }); if (start) { const ok = await ctx.startStack(); if (ok) window.open(r.url, '_blank'); } }
    } catch (err) {
      button.disabled = false;
      clear(errorNote).appendChild(note('bad', err.message));
      errorNote.classList.remove('hidden');
    }
  };
  const d = dialogs.open({ iconName: 'plus', title: 'Nuevo sitio', subtitle: 'Repetirlo sobre un sitio que ya existe solo lo actualiza.', wide: true, body: [
    h('div', h('div.lbl', 'Plantilla'), segmented([{ value: 'folder', label: 'Carpeta (lo de siempre)' }, { value: 'laravel', label: 'Laravel', disabled: true, title: 'Próximamente en el panel: roost new laravel <nombre>' }, { value: 'wordpress', label: 'WordPress', disabled: true, title: 'Próximamente: roost new wordpress <nombre>' }, { value: 'symfony', label: 'Symfony', disabled: true, title: 'Próximamente: roost new symfony <nombre>' }, { value: 'git', label: 'Desde git…', disabled: true, title: 'Próximamente: roost site clone <url>' }], 'folder', () => {}), h('div.hint', 'Una carpeta con un index.php, o la tuya con tu proyecto dentro: se sirve tal cual. Las plantillas y git, de momento desde la terminal.')),
    h('div.form-grid',
      h('div.form-row', h('label', 'Nombre'), name, nameHint),
      h('div.form-row', h('label', 'Carpeta'), folder, folderHint),
      h('div.form-row.full', h('label', 'Raíz web (dentro de la carpeta; opcional)'), rootIn, rootHint),
      h('div.form-row', h('label', 'PHP'), php, phpHint),
      h('div.form-row', h('label', 'Base de datos'), dbSeg, dbName, dbHint)),
    h('div.php-box', h('div.lbl', 'Al crear, Roost:'), summary),
    errorNote,
  ], footer: [h('div.right', btn('Cancelar', { onclick: () => d.close(false) }), btn('Crear y abrir', { class: 'primary', onclick: create }))] });
  update();
  return d.promise;
}
