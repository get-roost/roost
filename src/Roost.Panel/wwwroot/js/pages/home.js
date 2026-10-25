// Inicio: los accesos, la tabla de servicios, los sitios y la auditoría (HomeView de la app).
import { h, icon, btn, chip, dot, note, clear, table, shortPath, timeText, copy, toast } from '../ui.js';
import { onStatus } from '../app.js';

export async function render(root, ctx) {
  const { api, navigate } = ctx;
  const page = h('div.stack');
  root.appendChild(page);
  let unsub = null;
  const load = async () => {
    let home;
    try { home = await api.get('/home'); } catch (e) { clear(page).appendChild(note('bad', e.message)); return; }
    if (ctx.stale()) { if (unsub) unsub(); return; }
    draw(home);
  };
  const draw = (home) => {
    const s = ctx.state.status || {};
    const d = s.daemon;
    const running = d && d.running;
    clear(page);
    page.appendChild(h('div.page-head',
      h('div.titles', h('h1.h1', 'Inicio'), h('div.sub', home.summary)),
      h('div.actions',
        btn('Abrir localhost', { onclick: () => window.open(home.url, '_blank') }, icon('external', 15)),
        btn('Nuevo sitio', { class: 'primary', onclick: () => ctx.pageDef && import('./sites.js').then(m => m.newSite(ctx)) }, icon('plus', 15)))));

    const tile = (ic, title, text, onclick, tone) => h('button.tile', { type: 'button', onclick }, h('div.tile-ic' + (tone ? '.' + tone : ''), icon(ic, 16)), h('div', h('div.t', title), h('div.d.ellipsis', { title: text }, text)));
    page.appendChild(h('div.tiles',
      tile('globe', 'Web', home.tiles.web, () => window.open(home.url, '_blank')),
      tile('terminal', 'Terminal', home.tiles.terminal, () => toast('Desde el panel no se abre una terminal: usa la tuya (roost php which dice qué PHP toca).')),
      tile('folder', 'Carpeta www', home.tiles.www, () => copy(home.tiles.www)),
      tile('database', 'Base de datos', home.tiles.db, () => navigate('bd')),
      tile('shieldCheck', 'Auditoría', home.tiles.audit, () => navigate('auditoria'), home.audit && home.audit.red > 0 ? 'bad' : null)));

    // Lo que corre en segundo plano (instalaciones que se dejaron ir).
    for (const j of s.jobs || []) page.appendChild(note('info', `${j.title} en segundo plano: ${j.message || '…'}`));

    const act = async (path, label) => {
      try { const r = await api.post(path); if (!r.ok) throw new Error(r.error || 'No se pudo'); ctx.flash(label + (r.applied && r.applied.length ? ': ' + r.applied.join('; ') : '')); }
      catch (e) { toast(e.message, 'bad'); }
      await load();
    };
    const card = h('div.card',
      h('div.card-h', h('span.h2', 'Servicios'), h('span.hint', 'Arrancan en este orden y se paran al revés: base de datos, Redis y correo, PHP, servidor web'),
        h('div.right.row', btn('Reiniciar todo', { class: 'sm ghost', disabled: !running, onclick: () => act('/stack/restart', 'Reiniciado todo') }, icon('restart', 14)), btn('Ver registros', { class: 'sm ghost', onclick: () => navigate('registros') }))),
      ...table([
        { label: 'Servicio', width: '2.2fr', cell: r => h('div', h('div.row', h('span.strong', r.name), r.tag ? chip(r.tag, r.tagTone) : null), h('div.hint.ellipsis', { title: r.description }, r.description)) },
        { label: 'Versión', width: '1fr', cell: r => h('span.mono', r.version) },
        { label: 'Escucha en', width: '1fr', cell: r => h('span.mono', r.listen) },
        { label: 'Estado', width: '1.2fr', cell: r => chip(r.state, r.tone === 'off' ? '' : r.tone, true) },
        { label: 'Reinicios', width: '.6fr', align: 'right', cell: r => h('span.mono', String(r.restarts)) },
        { label: 'Acciones', width: '.8fr', align: 'right', cell: r => h('div.row', { class: 'right' },
          h('button.btn.sm.icon.ghost', { type: 'button', title: r.running ? 'Parar' : 'Arrancar', disabled: !running, onclick: () => act(`/stack/${r.running ? 'stop' : 'start'}/${encodeURIComponent(r.service)}`, (r.running ? 'Parado ' : 'Arrancado ') + r.service) }, icon(r.running ? 'stop' : 'play', 13, 2, true)),
          h('button.btn.sm.icon.ghost', { type: 'button', title: 'Reiniciar', disabled: !running, onclick: () => act(`/stack/restart/${encodeURIComponent(r.service)}`, 'Reiniciado ' + r.service) }, icon('restart', 13))) },
      ], home.services, { empty: 'Nada que arrancar: instala un PHP en la pantalla PHP.' }));
    page.appendChild(card);

    const sitesCard = h('div.card', h('div.card-h', h('span.h2', 'Sitios'), h('div.right', btn('Gestionar', { class: 'sm ghost', onclick: () => navigate('sitios') }))),
      home.sites.length ? home.sites.map(x => { const tr = h('div.tr', h('div.row', dot(x.running ? 'ok' : ''), h('a', { href: x.url, target: '_blank', rel: 'noreferrer' }, x.url)), h('div.right', chip(`PHP ${x.php}${x.eol ? ' · EOL' : ''}`, x.eol ? 'bad' : ''))); tr.style.gridTemplateColumns = '1fr auto'; return tr; }) : h('div.card-b.muted', 'Ningún sitio todavía. Nuevo sitio crea uno en https://<nombre>.test.'));
    const a = home.audit;
    const auditCard = h('div.card', h('div.card-h', h('span.h2', 'Auditoría'), h('span.hint', 'dataset del ' + home.datasetDate), h('div.right', btn('Ver todo', { class: 'sm ghost', onclick: () => navigate('auditoria') }))),
      !a ? h('div.card-b.muted', 'Sin escanear') : a.reds.length === 0 ? h('div.card-b', h('div.row', dot('ok'), `Todo al día (${a.green} en verde, ${a.amber} en ámbar)`)) : a.reds.map(x => { const tr = h('div.tr', h('div.row', dot('bad'), h('span', `${x.kind} ${x.version}`), h('span.muted', `(${x.source})`)), h('div.right', chip(x.chip, 'bad'))); tr.style.gridTemplateColumns = '1fr auto'; return tr; }));
    page.appendChild(h('div.grid.two', sitesCard, auditCard));
  };
  await load();
  unsub = onStatus(() => { if (ctx.stale()) { unsub(); return; } load(); });
}
