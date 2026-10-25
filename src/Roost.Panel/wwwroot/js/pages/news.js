// Novedades: el CHANGELOG empaquetado en Roost, versión a versión (NewsView de la app).
import { h, note, clear } from '../ui.js';

export async function render(root, ctx) {
  root.appendChild(h('div.page-head', h('div.titles', h('h1.h1', 'Novedades'), h('div.sub', 'Lo que cambia en cada versión de Roost.'))));
  let news;
  try { news = await ctx.api.get('/news'); } catch (e) { root.appendChild(note('bad', e.message)); return; }
  if (ctx.stale()) return;
  const list = h('div.stack');
  for (const e of news.entries) {
    list.appendChild(h('div.card', h('div.card-h', h('span.h2', 'Roost ' + e.version), h('span.hint', e.date || 'en preparación'), e.version === news.version ? h('span.chip.accent', 'esta versión') : null),
      h('div.card-b', h('ul', e.items.map(i => h('li', { html: i.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>').replace(/`(.+?)`/g, '<code>$1</code>') }))))));
  }
  root.appendChild(list);
}
