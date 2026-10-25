// Auditoría: el inventario evaluado contra los avisos, con filtros por color, tipo y origen, el detalle
// y exportar (AuditView de la app).
import { h, icon, btn, chip, dot, note, clear, table, shortPath, toast, segmented, dateText, timeText } from '../ui.js';

let filter = { color: 'all', kind: '', source: '', outside: true };
let selectedKey = null;

export async function render(root, ctx) {
  const { api, dialogs } = ctx;
  const page = h('div.stack');
  root.appendChild(page);
  let a;
  const load = async () => {
    try { a = await api.get('/audit'); } catch (e) { clear(page).appendChild(note('bad', e.message)); return; }
    if (ctx.stale()) return;
    ctx.state.redCount = a.red;
    draw();
  };
  const scan = async (feed = false) => {
    try { await dialogs.job(() => api.post('/audit/scan?feed=' + feed), { api }); } catch {}
    await load();
    ctx.navigate('auditoria');
  };
  const draw = () => {
    clear(page);
    page.appendChild(h('div.page-head', h('div.titles', h('h1.h1', 'Auditoría'), h('div.sub', 'Todo lo instalado en este equipo, de Roost o no, frente a los avisos de seguridad y las fechas de fin de vida.')),
      h('div.actions', btn('Exportar Markdown', { disabled: !a.scanned, onclick: () => api.download('/audit/export?format=md') }), btn('Exportar JSON', { disabled: !a.scanned, onclick: () => api.download('/audit/export?format=json') }), btn(a.scanned ? 'Volver a escanear' : 'Escanear', { class: a.scanned ? '' : 'primary', onclick: () => scan(false) }))));
    for (const j of a.jobs || []) page.appendChild(note('info', `${j.title}: ${j.message || '…'}`));
    const count = (label, n, text, tone) => h('div.card.card-b', h('div.row', chip(label, tone, tone !== '')), h('div.h1', String(n)), h('div.hint', text));
    page.appendChild(h('div.tiles', count('Rojo', a.red, 'vulnerable o fin de vida', 'bad'), count('Ámbar', a.amber, 'sin verificar o conviene actualizar', 'warn'), count('Verde', a.green, 'al día', 'ok'),
      h('div.card.card-b', h('div.row', chip('Avisos', '')), h('div.strong', dateText(a.dataset.generated) + ', ' + timeText(a.dataset.generated)), h('div.hint', a.dataset.originText + (a.dataset.note ? ' · ' + a.dataset.note : '')), btn('Actualizar el feed', { class: 'link sm', onclick: () => scan(true) }))));
    if (!a.scanned) { page.appendChild(h('div.card.card-b.muted', 'Sin escanear. Escanear busca PHP, MySQL, MariaDB, nginx y Apache en este equipo (Roost, Laragon, XAMPP, Program Files, el PATH…) y los evalúa en local.')); return; }

    const rows = a.findings.filter(f => (filter.color === 'all' || f.color === filter.color) && (!filter.kind || f.kind === filter.kind) && (!filter.source || f.source === filter.source) && (filter.outside || f.isRoost));
    const kindSel = h('select.input', h('option', { value: '' }, 'Todos los tipos'), a.kinds.map(k => h('option', { value: k, selected: filter.kind === k }, k)));
    kindSel.addEventListener('change', () => { filter.kind = kindSel.value; draw(); });
    const srcSel = h('select.input', h('option', { value: '' }, 'Todos los orígenes'), a.sources.map(k => h('option', { value: k, selected: filter.source === k }, k)));
    srcSel.addEventListener('change', () => { filter.source = srcSel.value; draw(); });
    const inv = h('div.card', h('div.card-h', h('span.h2', 'Inventario'), segmented([{ value: 'all', label: 'Todo' }, { value: 'red', label: 'Rojo' }, { value: 'amber', label: 'Ámbar' }, { value: 'green', label: 'Verde' }], filter.color, v => { filter.color = v; draw(); }), h('span.hint', `Escaneado a las ${timeText(a.when)} · ${a.os || ''}`),
        h('label.check.right', h('input', { type: 'checkbox', checked: filter.outside, onchange: (e) => { filter.outside = e.target.checked; draw(); } }), h('span', 'Incluir lo de fuera de Roost'))),
      h('div.card-b', h('div.row', h('span.lbl', 'Tipo'), kindSel, h('span.lbl', 'Origen'), srcSel)),
      ...table([
        { label: '', width: '20px', cell: f => dot(f.ignored ? '' : f.tone) },
        { label: 'Componente', width: '1.2fr', cell: f => h('div.row', h('span.strong', f.kind), h('span.mono', f.version)) },
        { label: 'Origen', width: '1fr', cell: f => h('span', f.source) },
        { label: 'Veredicto', width: '1.2fr', cell: f => chip(f.ignored ? 'ignorado' : f.chip, f.ignored ? '' : f.tone) },
        { label: 'Detalle', width: '2.5fr', cell: f => h('div.hint', f.headline) },
      ], rows, { onRow: f => { selectedKey = f.key; draw(); }, selected: f => f.key === selectedKey, empty: 'Nada con esos filtros.' }));
    page.appendChild(inv);
    const f = a.findings.find(x => x.key === selectedKey);
    if (f) page.appendChild(detail(f));
  };
  const detail = (f) => {
    const body = h('div.card-b.stack');
    body.appendChild(h('div', f.headline));
    body.appendChild(h('div.kv', h('div.k', 'Ruta'), h('div.mono.ellipsis', { title: f.path }, f.path), f.branchLatest ? h('div.k', 'Última de la rama') : null, f.branchLatest ? h('div.mono', f.branchLatest) : null, f.eol ? h('div.k', 'Fin de vida') : null, f.eol ? h('div', dateText(f.eol)) : null));
    if (f.advisories.length) body.appendChild(h('div', h('div.lbl', 'Avisos'), h('div.card', f.advisories.map(ad => { const tr = h('div.tr', h('div', h('div.row', h('a.mono', { href: ad.url, target: '_blank', rel: 'noreferrer' }, ad.id), ad.kev ? chip('KEV', 'bad') : null, ad.cvss ? chip('CVSS ' + ad.cvss, ad.cvss >= 9 ? 'bad' : ad.cvss >= 7 ? 'warn' : '') : ad.severity ? chip(ad.severity, 'warn') : null), h('div.hint', ad.title))); tr.style.gridTemplateColumns = '1fr'; return tr; }))));
    if (f.mitigations.length) body.appendChild(h('div', h('div.lbl', 'Mitigaciones'), h('div.hint', 'Se aplican con roost audit --apply-mitigations (próximamente desde el panel).'), h('div.card', f.mitigations.map(m => { const tr = h('div.tr', h('div', h('div.mono', `${m.ini} = ${m.value}`), m.note ? h('div.hint', m.note) : null), h('div.right', chip(m.status, ''))); tr.style.gridTemplateColumns = '1fr auto'; return tr; }))));
    if (f.notes && f.notes.length) body.appendChild(note('info', f.notes.join(' ')));
    return h('div.card', h('div.card-h', dot(f.tone), h('span.strong', `${f.kind} ${f.version} · ${f.source}`), chip(f.chip, f.tone),
      h('div.right.row', btn(f.ignored ? 'Dejar de ignorar' : 'Ignorar', { class: 'sm ghost', onclick: async () => { try { a = await api.post('/audit/ignore', { key: f.key, ignore: !f.ignored }); ctx.state.redCount = a.red; draw(); } catch (e) { toast(e.message, 'bad'); } } }),
        f.action ? btn('Instalar ' + f.action, { class: 'sm primary', onclick: async () => { try { await dialogs.job(() => api.post('/php/install', { spec: f.action }), { api }); ctx.navigate('php'); } catch {} } }) : null)), body);
  };
  await load();
}
