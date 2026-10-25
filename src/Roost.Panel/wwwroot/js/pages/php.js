// PHP: las instaladas, el detalle (php.ini, conf.d, pool, Xdebug, extensiones, ajustes rápidos) y las
// disponibles del catálogo; instalar, «Usar como global» y «Quitar versión» (PhpView de la app).
import { h, icon, btn, chip, dot, note, clear, table, shortPath, copy, toast, copyable, segmented, dateText } from '../ui.js';
import { onStatus } from '../app.js';

let selected = null;
let variant = { ts: false, arch: 'x64' };

export async function render(root, ctx) {
  const { api, dialogs } = ctx;
  const page = h('div.stack');
  root.appendChild(page);
  let data = null, detail = null;
  const load = async (withCatalog = true) => {
    try { data = await api.get('/php' + (withCatalog ? '?catalog=true' : '')); } catch (e) { clear(page).appendChild(note('bad', e.message)); return; }
    if (ctx.stale()) return;
    if (selected && !data.installed.some(p => p.name === selected)) selected = null;
    if (!selected && data.installed.length) selected = (data.installed.find(p => p.global) || data.installed[0]).name;
    detail = null;
    if (selected) { try { detail = await api.get('/php/' + encodeURIComponent(selected)); } catch (e) { detail = { error: e.message }; } }
    if (ctx.stale()) return;
    draw();
  };

  const draw = () => {
    clear(page);
    page.appendChild(h('div.page-head', h('div.titles', h('h1.h1', 'PHP'), h('div.sub', data.headline)),
      h('div.actions', btn('Comprobar catálogo', { onclick: async () => { try { data = await api.post('/php/catalog'); draw(); toast('Catálogo al día'); } catch (e) { toast(e.message, 'bad'); } } }))));
    for (const j of data.jobs || []) page.appendChild(note('info', `Instalando en segundo plano: ${j.title} · ${j.message || '…'}`));
    if (data.windowsOnly && data.windowsOnly.length) page.appendChild(note('info', data.windowsOnly.map(w => `PHP ${w.branch}: ${w.why}`).join(' ')));

    page.appendChild(h('div.row', h('span.h2', 'Instaladas por Roost'), h('span.hint', 'Global: la que sirve localhost y los sitios sin PHP propio')));
    const cards = h('div.vcards');
    for (const p of data.installed) {
      cards.appendChild(h('button.vcard' + (p.name === selected ? '.sel' : ''), { type: 'button', onclick: async () => { selected = p.name; detail = null; draw(); try { detail = await api.get('/php/' + encodeURIComponent(selected)); } catch (e) { detail = { error: e.message }; } if (!ctx.stale()) draw(); } },
        h('div.row', h('span.ver', p.version), p.global ? chip('global', 'accent') : null, h('span.right', dot(p.running ? 'ok' : ''))),
        h('div.hint', `${p.ts} · ${p.arch}${p.compiler ? ' · ' + p.compiler : ''}`),
        h('div', chip(p.chip, p.tone)),
        h('div.hint.ellipsis', { title: p.uses }, p.uses)));
    }
    if (!data.installed.length) cards.appendChild(h('div.card.card-b.muted', 'Ninguna todavía: instala una de la lista de la derecha.'));
    page.appendChild(cards);

    const left = h('div.stack');
    if (detail && !detail.error) left.appendChild(detailCard(detail));
    else if (detail && detail.error) left.appendChild(note('bad', detail.error));
    else if (selected) left.appendChild(h('div.card.card-b.muted', 'Cargando…'));
    page.appendChild(h('div.grid.main-side', left, availableCard()));
  };

  const applyNote = (r) => { if (r && r.applied) ctx.flash('Aplicado: ' + r.applied); else ctx.flash('Guardado (se aplica en el próximo Iniciar todo o cuando lo use un sitio)'); };

  const detailCard = (d) => {
    const body = h('div.card-b.stack');
    body.appendChild(h('div.kv',
      h('div.k', 'Carpeta'), copyable(d.directory),
      h('div.k', 'php.ini'), h('div.row', h('span.mono.ellipsis', { title: d.ini }, d.ini), btn('Ver', { class: 'link right', onclick: () => showText('ini') })),
      h('div.k', 'Tus ajustes'), h('div', h('div.row', h('span.mono.ellipsis', shortPath(d.confdCommon, 36)), h('span.hint', 'todas las versiones')), h('div.row', h('span.mono.ellipsis', shortPath(d.confdBranch, 36)), h('span.hint', `solo la ${d.branch}: va después y manda`))),
      h('div.k', `Pool ${d.pool.kind}`), h('div.mono', d.pool.count ? `${d.pool.count} ${d.pool.kind === 'php-fpm' ? 'proceso' : 'procesos'} · ${d.pool.ports} · ${d.pool.running ? 'en marcha' : 'parado'}` : `${d.pool.kind === 'php-fpm' ? 'php-fpm' : d.pool.planned + ' procesos'} · parado`),
      h('div.k', 'Diagnóstico'), h('div.row', btn('phpinfo()', { class: 'sm', onclick: () => showText('phpinfo') }), btn('Módulos', { class: 'sm', onclick: () => showText('modules') }), btn('Copiar launch.json', { class: 'sm', onclick: () => copy(d.launchJson) }))));
    const sw = h('button.switch' + (d.xdebug.on ? '.on' : ''), { type: 'button', disabled: !d.xdebug.available, 'aria-label': 'Xdebug', onclick: async () => {
      sw.disabled = true;
      try { await dialogs.job(() => api.post(`/php/${encodeURIComponent(d.name)}/xdebug`, { on: !d.xdebug.on }), { api }); } catch {}
      await load(false);
    } });
    body.appendChild(h('div.php-box', h('div.row', sw, h('div', h('div.strong', 'Xdebug'), h('div.hint', d.xdebug.text)))));
    const exts = h('div.row.wrap');
    for (const e of d.extensions) {
      const b = h('button.btn.ext' + (e.on ? '.on' : '.dashed') + (e.builtIn ? '.fixed' : ''), { type: 'button', title: e.builtIn ? 'Integrada en este PHP: siempre activa' : e.zend ? 'Extensión Zend' : (e.on ? 'Quitar' : 'Activar'), disabled: e.builtIn, onclick: async () => {
        b.disabled = true;
        try { const r = await api.post(`/php/${encodeURIComponent(d.name)}/ext`, { ext: e.name, on: !e.on }); if (r.warnings && r.warnings.length) toast(r.warnings.join(' '), 'bad'); else applyNote(r); }
        catch (err) { toast(err.message, 'bad'); }
        await load(false);
      } }, e.name);
      exts.appendChild(b);
    }
    body.appendChild(h('div', h('div.row', h('span.lbl', 'Extensiones'), h('span.hint', 'Pulsa para activar o quitar; se comprueba con php -v y se reinicia solo este pool.')), exts));
    const s = d.settings;
    const fields = { memoryLimit: h('input.input.mono', { value: s.memoryLimit }), uploadMaxFilesize: h('input.input.mono', { value: s.uploadMaxFilesize }), postMaxSize: h('input.input.mono', { value: s.postMaxSize }), maxExecutionTime: h('input.input.mono', { value: String(s.maxExecutionTime), type: 'number', min: '0' }) };
    const grid = h('div.form-grid');
    grid.style.gridTemplateColumns = 'repeat(4, 1fr)';
    grid.append(h('div.form-row', h('label', 'memory_limit'), fields.memoryLimit), h('div.form-row', h('label', 'upload_max_filesize'), fields.uploadMaxFilesize), h('div.form-row', h('label', 'post_max_size'), fields.postMaxSize), h('div.form-row', h('label', 'max_execution_time (s)'), fields.maxExecutionTime));
    body.appendChild(h('div', h('div.row', h('span.lbl', 'Ajustes rápidos'), h('span.hint', 'De esta versión, en su php.ini. Lo de conf.d manda sobre esto.')), grid,
      h('div.row', btn('Guardar', { class: 'primary sm', onclick: async (e) => { e.target.disabled = true; try { const r = await api.post(`/php/${encodeURIComponent(d.name)}/settings`, { memoryLimit: fields.memoryLimit.value, uploadMaxFilesize: fields.uploadMaxFilesize.value, postMaxSize: fields.postMaxSize.value, maxExecutionTime: Number(fields.maxExecutionTime.value) }); applyNote(r); } catch (err) { toast(err.message, 'bad'); } await load(false); } }),
        btn('De serie', { class: 'sm', onclick: () => { fields.memoryLimit.value = d.defaults.memoryLimit; fields.uploadMaxFilesize.value = d.defaults.uploadMaxFilesize; fields.postMaxSize.value = d.defaults.postMaxSize; fields.maxExecutionTime.value = d.defaults.maxExecutionTime; } }),
        h('span.hint', '-1 en memory_limit y 0 en max_execution_time quitan el límite. post_max_size tiene que ser ≥ la subida.'))));
    return h('div.card', h('div.card-h', h('span.mono.strong', 'PHP ' + d.version), chip(d.chip, d.tone),
      h('div.right.row', btn('Usar como global', { class: 'sm', disabled: d.global, onclick: async () => { try { const r = await api.post('/php/use', { name: d.name }); if (!r.ok) throw new Error(r.error); ctx.flash(`PHP ${d.branch} es el global`); } catch (e) { toast(e.message, 'bad'); } await load(false); } }),
        btn('Quitar', { class: 'sm', onclick: () => removePhp(d) }))), body);
  };

  const showText = async (what) => {
    try { const t = await api.get(`/php/${encodeURIComponent(selected)}/text?what=${what}`); dialogs.text(t.title, t.subtitle, t.text); }
    catch (e) { toast(e.message, 'bad'); }
  };

  const removePhp = async (d) => {
    let info;
    try { info = await api.post('/php/remove', { name: d.name }); } catch (e) { toast(e.message, 'bad'); return; }
    if (info.removed) { ctx.flash(`Quitado PHP ${d.version}`); await load(false); return; }
    // «Quitar versión»: la usa un sitio (o es la global): a qué PHP pasan.
    let choice = info.global ? 'global' : (info.others[0] ? info.others[0].name : null);
    const other = h('select.input', info.others.map(o => h('option', { value: o.name }, o.version)));
    other.disabled = !info.others.length;
    const radios = h('div.col');
    const radio = (value, label, hint, extra) => h('label.radio', h('input', { type: 'radio', name: 'remove-php', checked: choice === value, disabled: value === null, onchange: () => { choice = value; } }), h('div', h('div', label, extra), hint ? h('div.hint', hint) : null));
    if (info.global) radios.appendChild(radio('global', `Pasarlo al PHP global ${info.global.version}`, 'Revisa que el proyecto funciona con esa versión: entre ramas hay cambios que rompen.'));
    radios.appendChild(radio(info.others.length ? 'other' : null, 'Pasarlo a otra versión instalada ', null, other));
    radios.appendChild(radio('keep', `No quitar PHP ${info.version} todavía`));
    const rows = info.sites.map(s => h('div.row', h('span.mono.strong', s.host), h('span.hint.ellipsis', shortPath(s.folder, 36)), s.pinned ? h('span.right', chip(`PHP ${d.branch} fijado`, 'bad')) : null));
    const who = info.isGlobal && !info.sites.length ? 'Es el PHP global.' : `Lo usa${info.sites.length === 1 ? '' : 'n'} ${info.sites.length} sitio${info.sites.length === 1 ? '' : 's'}.`;
    const ok = await dialogs.confirm(`PHP ${info.version} está en uso`, h('div.col', h('div.sub', who + ' Antes de quitarlo hay que decidir qué PHP usará' + (info.isGlobal ? ' el global' : 'n') + '.'), rows.length ? h('div.php-box.col', rows) : null, h('div.lbl', info.sites.length ? `¿Qué hacemos con ${info.sites.map(s => s.host).join(', ')}?` : '¿Qué PHP pasa a ser el global?'), radios, note('info', `Quitar una versión borra solo su carpeta (${info.directory}). Tus ajustes de conf.d se quedan.`)), { ok: `Pasar y quitar ${info.version}`, danger: true, iconName: 'warning' });
    if (!ok || choice === 'keep' || choice === null) return;
    try { const r = await api.post('/php/remove', { name: d.name, sitesTo: choice === 'other' ? other.value : 'global' }); if (r.removed) ctx.flash(`Quitado PHP ${info.version}`); }
    catch (e) { toast(e.message, 'bad'); }
    await load(false);
  };

  const availableCard = () => {
    const body = h('div');
    if (data.variants) body.appendChild(h('div.card-b', h('div.row', segmented([{ value: 'nts', label: 'NTS' }, { value: 'ts', label: 'TS' }], variant.ts ? 'ts' : 'nts', v => { variant.ts = v === 'ts'; }), segmented([{ value: 'x64', label: 'x64' }, { value: 'x86', label: 'x86' }], variant.arch, v => { variant.arch = v; })), h('div.hint', 'NTS: la que usa Roost (php-cgi, con nginx o Apache). Recomendada.')));
    if (!data.available) body.appendChild(h('div.card-b', data.catalogProblem ? note('warn', 'No se pudo leer el catálogo: ' + data.catalogProblem) : h('div.muted', 'Cargando el catálogo…')));
    else for (const a of data.available) {
      const tr = h('div.tr', h('div.row', dot(a.tone), h('div', h('div.mono.strong', a.version), h('div.hint', a.support))), h('div.right', a.installed ? chip('instalada', 'ok') : btn('Instalar', { class: 'sm', onclick: () => install(a.version) })));
      tr.style.gridTemplateColumns = '1fr auto';
      body.appendChild(tr);
    }
    return h('div.card', h('div.card-h', h('span.h2', 'Disponibles'), h('span.hint', data.source + (data.catalogWhen ? ' · ' + dateText(data.catalogWhen) : ''))), body);
  };

  const install = async (spec) => {
    try { await dialogs.job(() => api.post('/php/install', { spec, ts: variant.ts, arch: variant.arch }), { api, onDone: () => load(false) }); }
    catch {}
    await load(false);
  };

  await load(true);
  const unsub = onStatus(() => { if (ctx.stale()) { unsub(); return; } load(false); });
}
