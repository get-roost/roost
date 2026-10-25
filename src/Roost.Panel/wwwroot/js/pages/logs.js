// Registros: los ficheros de logs/ con sus errores y avisos, el final de cada uno con color por nivel,
// filtrar, seguir y por sitio (LogsView de la app).
import { h, icon, btn, chip, dot, note, clear, copy, toast, shortPath } from '../ui.js';

let selectedId = null;
let follow = true;

export async function render(root, ctx) {
  const { api } = ctx;
  if (ctx.params.file) selectedId = ctx.params.file;
  let siteFilter = ctx.params.site || '';
  const page = h('div.stack');
  root.appendChild(page);
  let data, lines = [], size = -1, filter = '';
  let timer = null;
  const stop = () => { if (timer) { clearInterval(timer); timer = null; } };
  const load = async () => {
    try { data = await api.get('/logs'); } catch (e) { clear(page).appendChild(note('bad', e.message)); return; }
    if (ctx.stale()) { stop(); return; }
    if (siteFilter && !data.files.some(f => f.site === siteFilter)) siteFilter = '';
    const files = visible();
    if (selectedId && !files.some(f => f.id === selectedId)) selectedId = null;
    if (!selectedId && files.length) selectedId = files[0].id;
    await read(false);
    draw();
  };
  const visible = () => data.files.filter(f => siteFilter ? f.site === siteFilter : true);
  const read = async (onlyIfChanged) => {
    if (!selectedId) { lines = []; return; }
    try {
      const r = await api.get(`/logs/read?id=${encodeURIComponent(selectedId)}&tail=500`);
      if (onlyIfChanged && r.size === size) return false;
      size = r.size; lines = r.lines; return true;
    } catch (e) { lines = [{ text: e.message, level: 'e' }]; return true; }
  };
  const pre = h('pre.log.fill');
  const renderLines = () => {
    clear(pre);
    const q = filter.toLowerCase();
    const shown = q ? lines.filter(l => l.text.toLowerCase().includes(q)) : lines;
    if (!shown.length) pre.appendChild(h('span.m', lines.length ? '(sin coincidencias)' : '(vacío)'));
    for (const l of shown) { pre.appendChild(h('span' + (l.level !== 'n' ? '.' + l.level : ''), l.text)); pre.appendChild(document.createTextNode('\n')); }
    if (follow) pre.scrollTop = pre.scrollHeight;
  };
  const draw = () => {
    clear(page);
    page.appendChild(h('div.page-head', h('div.titles', h('h1.h1', 'Registros'), h('div.sub', 'Lo que escriben Roost y cada servicio. Están en ' + data.logs)),
      h('div.actions', btn('Copiar la ruta', { onclick: () => copy(data.logs) }))));
    const sites = [...new Set(data.files.filter(f => f.site).map(f => f.site))];
    const sel = h('select.input', h('option', { value: '' }, 'Todos los registros'), sites.map(s => h('option', { value: s, selected: siteFilter === s }, s + '.test')));
    sel.addEventListener('change', () => { siteFilter = sel.value; selectedId = null; load(); });
    const list = h('div.card', h('div.card-h', h('span.h2', 'Ficheros')), h('div.card-b', sel),
      visible().map(f => h('div.list-item' + (f.id === selectedId ? '.sel' : ''), { onclick: async () => { selectedId = f.id; size = -1; await read(false); draw(); } }, h('div.grow', h('div.t', f.title), h('div.d.ellipsis', { title: f.path }, f.display)), f.errors ? chip(`${f.errors} ${f.errors === 1 ? 'error' : 'errores'}`, 'bad') : f.warnings ? chip(`${f.warnings} ${f.warnings === 1 ? 'aviso' : 'avisos'}`, 'warn') : null)));
    const file = data.files.find(f => f.id === selectedId);
    const search = h('input.input', { placeholder: 'texto', value: filter, oninput: (e) => { filter = e.target.value; renderLines(); } });
    const followCheck = h('label.check', h('input', { type: 'checkbox', checked: follow, onchange: (e) => { follow = e.target.checked; } }), h('span', 'Seguir'));
    const view = h('div.card', h('div.card-h', h('span.mono.strong', file ? file.display : '—'), h('div.right.row', h('span.lbl', 'Filtrar'), search, followCheck, h('button.btn.icon.sm', { type: 'button', title: 'Copiar', onclick: () => copy(lines.map(l => l.text).join('\n')) }, icon('copy', 14)))), h('div.card-b', pre));
    renderLines();
    page.appendChild(h('div.grid.side-main', list, view));
  };
  await load();
  stop();
  timer = setInterval(async () => {
    if (ctx.stale()) { stop(); return; }
    if (!follow) return;
    if (await read(true)) renderLines();
  }, 1000);
}
