// Piezas de la interfaz: el constructor de nodos, los iconos de la app (Icons.axaml, en una caja de 24×24,
// trazo de 1.8), chips, puntos, avisos y utilidades de formato.

export const ICONS = {
  brand: 'M4 12 L12 5 L20 12 M7 11 V19 H17 V11 M10 19 V15 H14 V19',
  home: 'M3 11 L12 4 L21 11 M5 10 V20 H19 V10',
  globe: 'M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M3 12 H21 M12 3 C14.5 5.7 15.8 8.7 15.8 12 C15.8 15.3 14.5 18.3 12 21 C9.5 18.3 8.2 15.3 8.2 12 C8.2 8.7 9.5 5.7 12 3 Z',
  code: 'M8 8 L4 12 L8 16 M16 8 L20 12 L16 16 M14 5 L10 19',
  server: 'M5 4 H19 A2 2 0 0 1 21 6 V9 A2 2 0 0 1 19 11 H5 A2 2 0 0 1 3 9 V6 A2 2 0 0 1 5 4 Z M5 13 H19 A2 2 0 0 1 21 15 V18 A2 2 0 0 1 19 20 H5 A2 2 0 0 1 3 18 V15 A2 2 0 0 1 5 13 Z M7 7.5 H7.01 M7 16.5 H7.01',
  database: 'M4 6 A8 3 0 1 0 20 6 A8 3 0 1 0 4 6 Z M4 6 V18 C4 19.7 7.6 21 12 21 C16.4 21 20 19.7 20 18 V6 M4 12 C4 13.7 7.6 15 12 15 C16.4 15 20 13.7 20 12',
  shieldCheck: 'M12 3 L5 6 V12 C5 16.4 8 19.7 12 21 C16 19.7 19 16.4 19 12 V6 Z M9 12 L11 14 L15 10',
  shieldAlert: 'M12 3 L5 6 V12 C5 16.4 8 19.7 12 21 C16 19.7 19 16.4 19 12 V6 Z M12 9 V13 M12 16 H12.01',
  file: 'M6 4 H15 L18 7 V20 H6 Z M9 11 H15 M9 15 H15',
  sliders: 'M4 6 H13 M17 6 H20 M4 12 H7 M11 12 H20 M4 18 H15 M19 18 H20 M17 6 A2 2 0 1 1 13 6 A2 2 0 1 1 17 6 Z M11 12 A2 2 0 1 1 7 12 A2 2 0 1 1 11 12 Z M19 18 A2 2 0 1 1 15 18 A2 2 0 1 1 19 18 Z',
  stop: 'M8 6 H16 A2 2 0 0 1 18 8 V16 A2 2 0 0 1 16 18 H8 A2 2 0 0 1 6 16 V8 A2 2 0 0 1 8 6 Z',
  play: 'M7 5 V19 L18 12 Z',
  close: 'M6 6 L18 18 M18 6 L6 18',
  minus: 'M5 12 H19', plus: 'M12 5 V19 M5 12 H19',
  warning: 'M12 4 L2.5 20 H21.5 Z M12 10 V14 M12 17 H12.01',
  info: 'M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M12 11 V16 M12 8 H12.01',
  error: 'M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M12 8 V13 M12 16 H12.01',
  restart: 'M20 11 A8 8 0 0 0 5.4 6.5 L4 8 M4 4 V8 H8 M4 13 A8 8 0 0 0 18.6 17.5 L20 16 M20 20 V16 H16',
  terminal: 'M5 4 H19 A2 2 0 0 1 21 6 V18 A2 2 0 0 1 19 20 H5 A2 2 0 0 1 3 18 V6 A2 2 0 0 1 5 4 Z M7 9 L10 12 L7 15 M13 15 H17',
  copy: 'M10 8 H18 A2 2 0 0 1 20 10 V18 A2 2 0 0 1 18 20 H10 A2 2 0 0 1 8 18 V10 A2 2 0 0 1 10 8 Z M16 8 V5 A1 1 0 0 0 15 4 H5 A1 1 0 0 0 4 5 V15 A1 1 0 0 0 5 16 H8',
  folder: 'M3 7 A2 2 0 0 1 5 5 H9 L11 7 H19 A2 2 0 0 1 21 9 V17 A2 2 0 0 1 19 19 H5 A2 2 0 0 1 3 17 Z',
  download: 'M12 4 V15 M7 10 L12 15 L17 10 M5 20 H19',
  check: 'M5 12 L10 17 L19 7',
  dot: 'M17 12 A5 5 0 1 1 7 12 A5 5 0 1 1 17 12 Z',
  external: 'M14 4 H20 V10 M20 4 L11 13 M18 14 V19 A1 1 0 0 1 17 20 H5 A1 1 0 0 1 4 19 V7 A1 1 0 0 1 5 6 H10',
  lock: 'M7 11 H17 A2 2 0 0 1 19 13 V19 A2 2 0 0 1 17 21 H7 A2 2 0 0 1 5 19 V13 A2 2 0 0 1 7 11 Z M8 11 V8 A4 4 0 0 1 16 8 V11',
  trash: 'M4 7 H20 M9 7 V4 H15 V7 M6 7 L7 20 H17 L18 7',
  search: 'M17 11 A6 6 0 1 1 5 11 A6 6 0 1 1 17 11 Z M15.5 15.5 L20 20',
  chevronRight: 'M9 6 L15 12 L9 18',
  eye: 'M2 12 C4.5 7.5 8 5 12 5 C16 5 19.5 7.5 22 12 C19.5 16.5 16 19 12 19 C8 19 4.5 16.5 2 12 Z M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12 Z',
  user: 'M16 8 A4 4 0 1 1 8 8 A4 4 0 1 1 16 8 Z M4 21 C4 17 7.6 14.5 12 14.5 C16.4 14.5 20 17 20 21',
  key: 'M14 10 A4 4 0 1 1 15.2 12.8 L8 20 H4 V16 L11.2 8.8 A4 4 0 0 1 14 10 Z M16 8 H16.01',
  gift: 'M20 12 V21 H4 V12 M2 7 H22 V12 H2 Z M12 21 V7 M12 7 H7.5 A2.5 2.5 0 0 1 7.5 2 C11 2 12 7 12 7 Z M12 7 H16.5 A2.5 2.5 0 0 0 16.5 2 C13 2 12 7 12 7 Z',
  package: 'M12 3 L20 7.5 V16.5 L12 21 L4 16.5 V7.5 Z M4 7.5 L12 12 L20 7.5 M12 12 V21 M8 5.25 L16 9.75',
  layers: 'M12 3 L21 8 L12 13 L3 8 Z M3 12 L12 17 L21 12 M3 16 L12 21 L21 16',
  mail: 'M5 5 H19 A2 2 0 0 1 21 7 V17 A2 2 0 0 1 19 19 H5 A2 2 0 0 1 3 17 V7 A2 2 0 0 1 5 5 Z M3 7 L12 13 L21 7',
  cog: 'M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12 Z M12 3 V5.5 M12 18.5 V21 M3 12 H5.5 M18.5 12 H21 M5.6 5.6 L7.4 7.4 M16.6 16.6 L18.4 18.4 M5.6 18.4 L7.4 16.6 M16.6 7.4 L18.4 5.6',
  gitBranch: 'M6 3 V15 M21 6 A3 3 0 1 1 15 6 A3 3 0 1 1 21 6 Z M9 18 A3 3 0 1 1 3 18 A3 3 0 1 1 9 18 Z M18 9 A9 9 0 0 1 9 18',
  history: 'M3 12 A9 9 0 1 0 12 3 A9.75 9.75 0 0 0 5.26 5.74 L3 8 M3 3 V8 H8 M12 7 V12 L16 14',
  node: 'M12 3 L20 7.5 V16.5 L12 21 L4 16.5 V7.5 Z M9.5 15 V9.5 L14.5 14.5 V9',
  upload: 'M12 15 V4 M7 9 L12 4 L17 9 M5 20 H19',
  share: 'M21 5 A3 3 0 1 1 15 5 A3 3 0 1 1 21 5 Z M9 12 A3 3 0 1 1 3 12 A3 3 0 1 1 9 12 Z M21 19 A3 3 0 1 1 15 19 A3 3 0 1 1 21 19 Z M8.6 13.5 L15.4 17.5 M15.4 6.5 L8.6 10.5',
};

const SVG = 'http://www.w3.org/2000/svg';

/** Un icono de la app como SVG en línea. */
export function icon(name, size = 16, stroke = 1.8, filled = false) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', size);
  svg.setAttribute('height', size);
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS(SVG, 'path');
  path.setAttribute('d', ICONS[name] || ICONS.info);
  path.setAttribute('fill', filled ? 'currentColor' : 'none');
  path.setAttribute('stroke', filled ? 'none' : 'currentColor');
  path.setAttribute('stroke-width', stroke);
  path.setAttribute('stroke-linecap', 'round');
  path.setAttribute('stroke-linejoin', 'round');
  svg.appendChild(path);
  return svg;
}

/**
 * h('div.card.sel#id', { attrs }, ...children): un nodo con clases, atributos (on* son manejadores,
 * data-* y aria-* tal cual) e hijos (texto, nodos, arrays o null).
 */
export function h(tag, attrs, ...children) {
  if (attrs && (typeof attrs !== 'object' || attrs instanceof Node || Array.isArray(attrs))) { children.unshift(attrs); attrs = null; }
  const [name, ...rest] = tag.split(/(?=[.#])/);
  const el = document.createElement(name || 'div');
  for (const part of rest) {
    if (part[0] === '.') el.classList.add(part.slice(1));
    else if (part[0] === '#') el.id = part.slice(1);
  }
  if (attrs) {
    for (const [key, value] of Object.entries(attrs)) {
      if (value == null || value === false) continue;
      if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2).toLowerCase(), value);
      else if (key === 'class') el.className += (el.className ? ' ' : '') + value;
      else if (key === 'html') el.innerHTML = value;
      else if (key === 'value' || key === 'checked' || key === 'disabled' || key === 'selected' || key === 'readOnly') el[key] = value;
      else el.setAttribute(key, value === true ? '' : value);
    }
  }
  append(el, children);
  return el;
}

export function append(el, children) {
  for (const child of children) {
    if (child == null || child === false) continue;
    if (Array.isArray(child)) append(el, child);
    else if (child instanceof Node) el.appendChild(child);
    else el.appendChild(document.createTextNode(String(child)));
  }
  return el;
}

export function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); return el; }

export const dot = (tone) => h('span.dot' + (tone ? '.' + tone : ''));
export const chip = (text, tone, withDot = false) => h('span.chip' + (tone ? '.' + tone : ''), withDot ? dot(tone) : null, text);
export function note(tone, ...content) {
  const name = tone === 'ok' ? 'check' : tone === 'warn' ? 'warning' : tone === 'bad' ? 'error' : 'info';
  return h('div.note' + (tone && tone !== 'info' ? '.' + tone : ''), icon(name, 16), h('div.grow', ...content));
}
export const btn = (text, attrs, ...children) => h('button.btn', { type: 'button', ...(attrs || {}) }, ...children, text);
export const iconBtn = (name, title, attrs) => h('button.btn.icon', { type: 'button', title, 'aria-label': title, ...(attrs || {}) }, icon(name, 15));

/** Un trozo de texto en mono, con «Copiar» al lado. */
export function copyable(text, attrs) {
  return h('span.row', attrs, h('span.mono.ellipsis', { title: text }, text), iconBtn('copy', 'Copiar', { class: 'sm', onclick: () => copy(text) }));
}

export async function copy(text) {
  try { await navigator.clipboard.writeText(text); toast('Copiado'); }
  catch { toast('No se pudo copiar (el navegador no lo permite aquí). Selecciónalo y copia a mano.', 'bad'); }
}

let toastTimer = null;
export function toast(text, tone) {
  document.querySelectorAll('.toast').forEach(t => t.remove());
  const el = h('div.toast' + (tone ? '.' + tone : ''), icon(tone === 'bad' ? 'error' : 'check', 16), h('div', text));
  document.body.appendChild(el);
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.remove(), tone === 'bad' ? 7000 : 2500);
}

/** Acorta una ruta por el principio («…\www\tienda»), como la app. */
export function shortPath(path, max = 40) {
  if (!path || path.length <= max) return path || '';
  return '…' + path.slice(path.length - max + 1);
}

export function plural(n, one, many) { return `${n} ${n === 1 ? one : many}`; }

export function timeText(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return d.toLocaleTimeString('es-ES', { hour: '2-digit', minute: '2-digit' });
}

export function dateText(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return d.toLocaleDateString('es-ES', { day: '2-digit', month: 'short', year: 'numeric' }).replace(/\./g, '').replace(/ /g, '-');
}

/** Un selector segmentado: options [{value, label, disabled}], onchange(value). */
export function segmented(options, value, onchange, attrs) {
  const seg = h('div.seg', attrs);
  for (const o of options) {
    const b = h('button', { type: 'button', class: o.value === value ? 'on' : '', disabled: o.disabled, title: o.title }, o.label);
    b.addEventListener('click', () => { seg.querySelectorAll('button').forEach(x => x.classList.remove('on')); b.classList.add('on'); onchange(o.value); });
    seg.appendChild(b);
  }
  return seg;
}

export function table(columns, rows, { onRow, selected, empty } = {}) {
  const cols = columns.map(c => c.width || '1fr').join(' ');
  const head = h('div.th'); head.style.gridTemplateColumns = cols;
  for (const c of columns) head.appendChild(h('div', { class: c.align === 'right' ? 'right' : '' }, c.label));
  const body = h('div');
  if (rows.length === 0 && empty) body.appendChild(h('div.card-b.muted', empty));
  for (const r of rows) {
    const tr = h('div.tr' + (onRow ? '.click' : '') + (selected && selected(r) ? '.sel' : ''), onRow ? { onclick: () => onRow(r) } : null);
    tr.style.gridTemplateColumns = cols;
    for (const c of columns) tr.appendChild(h('div', { class: (c.align === 'right' ? 'right ' : '') + (c.class || '') }, c.cell(r)));
    body.appendChild(tr);
  }
  return [head, body];
}
