// Los diálogos sobre la ventana (DialogViewModel en la app): mensaje, confirmación, texto de solo lectura,
// «Instalando» (una tarea larga con sus pasos), «Error de arranque», «Confiar CA» y «Bienvenida».
import { h, icon, btn, note, chip, dot, clear, toast, shortPath, plural, copy } from './ui.js';
import { waitJob } from './api.js';

const stack = [];
const layer = () => document.getElementById('dialogs');

export function isOpen() { return stack.length > 0; }

/** Abre un diálogo; devuelve {close(result), promise}. El último abierto es el que se ve. */
export function open({ iconName, iconTone, title, subtitle, body, footer, wide, xwide, dismissable = true, onClose }) {
  const l = layer();
  let resolve;
  const promise = new Promise(r => { resolve = r; });
  const modal = h('div.modal' + (wide ? '.wide' : '') + (xwide ? '.xwide' : ''), { role: 'dialog', 'aria-modal': 'true', 'aria-label': title });
  const entry = { modal, close: (result) => { if (!stack.includes(entry)) return; stack.splice(stack.indexOf(entry), 1); modal.remove(); if (stack.length === 0) l.classList.remove('open'); else stack[stack.length - 1].modal.classList.remove('hidden'); if (onClose) onClose(result); resolve(result); }, dismissable };
  const head = h('div.modal-h');
  if (iconName) head.appendChild(h('div.badge-ic' + (iconTone ? '.' + iconTone : ''), icon(iconName, 18)));
  head.appendChild(h('div.titles', h('div.h3', title), subtitle ? h('div.sub', subtitle) : null));
  if (dismissable) head.appendChild(h('button.btn.ghost.icon.sm.close', { type: 'button', title: 'Cerrar', 'aria-label': 'Cerrar', onclick: () => entry.close(null) }, icon('close', 14)));
  modal.append(head, h('div.modal-b', body));
  if (footer) modal.appendChild(h('div.modal-f', footer));
  for (const e of stack) e.modal.classList.add('hidden');
  stack.push(entry);
  l.appendChild(modal);
  l.classList.add('open');
  const first = modal.querySelector('input, select, textarea, button.primary, button');
  if (first) setTimeout(() => first.focus(), 30);
  entry.promise = promise;
  return entry;
}

export function dismiss() { const top = stack[stack.length - 1]; if (top && top.dismissable) top.close(null); }

export function message(title, text, tone) {
  const d = open({ iconName: tone === 'bad' ? 'error' : tone === 'warn' ? 'warning' : 'info', iconTone: tone === 'bad' ? 'bad' : tone === 'warn' ? 'warn' : '', title, body: [h('div', text)], footer: [h('div.right', btn('Cerrar', { class: 'primary', onclick: () => d.close(true) }))] });
  return d.promise;
}

export function confirm(title, text, { ok = 'Aceptar', cancel = 'Cancelar', danger = false, iconName = 'warning', extra } = {}) {
  const d = open({ iconName, iconTone: danger ? 'bad' : 'warn', title, body: [typeof text === 'string' ? h('div', text) : text, extra], footer: [h('div.right', btn(cancel, { onclick: () => d.close(false) }), btn(ok, { class: danger ? 'danger-fill' : 'primary', onclick: () => d.close(true) }))] });
  return d.promise;
}

/** Texto de solo lectura con búsqueda (php.ini, phpinfo, nginx.conf…). */
export function text(title, subtitle, content) {
  const pre = h('pre.log.fill', content || '(vacío)');
  const search = h('input.input', { placeholder: 'buscar', oninput: () => highlight() });
  function highlight() {
    const q = search.value.trim();
    clear(pre);
    if (!q) { pre.textContent = content || '(vacío)'; return; }
    const lines = (content || '').split('\n');
    const matching = lines.filter(l => l.toLowerCase().includes(q.toLowerCase()));
    pre.textContent = matching.length ? matching.join('\n') : '(sin coincidencias)';
  }
  const d = open({ iconName: 'file', title, subtitle, xwide: true, body: [h('div.row', search, btn('Copiar todo', { class: 'sm', onclick: () => copy(content || '') })), pre], footer: [h('div.right', btn('Cerrar', { onclick: () => d.close(true) }))] });
  return d.promise;
}

/**
 * «Instalando»: arranca la tarea (start() → {job}) y enseña sus pasos y su progreso. Resuelve con la vista
 * final; si se pasa a segundo plano, resuelve con null y la tarea sigue (las pantallas la ven en jobs).
 */
export async function job(start, { api, onDone } = {}) {
  let view;
  try { view = (await start()).job; }
  catch (e) { await message('No se pudo empezar', e.message, 'bad'); throw e; }
  const bar = h('div.progress.indeterminate', h('div'));
  const msg = h('div.lbl', view.message || '');
  const steps = h('div.col');
  const errorNote = h('div.hidden');
  let background = false, finished = false, final = null;
  const render = (v) => {
    msg.textContent = v.message || (v.state === 'done' ? 'Hecho.' : v.state === 'failed' ? 'Falló.' : v.state === 'cancelled' ? 'Cancelado.' : '…');
    if (v.progress != null) { bar.classList.remove('indeterminate'); bar.firstChild.style.width = Math.round(v.progress * 100) + '%'; }
    if (v.state !== 'running') { bar.classList.remove('indeterminate'); bar.firstChild.style.width = v.state === 'done' ? '100%' : bar.firstChild.style.width; }
    clear(steps);
    for (const s of v.steps) steps.appendChild(h('div.step.' + s.state, h('div.ic', s.state === 'done' ? icon('check', 11, 2.5) : s.state === 'failed' ? icon('close', 11, 2.5) : icon('dot', 10, 0, true)), h('div', h('div.t', s.label), s.detail ? h('div.d', s.detail) : null)));
    if (v.state === 'failed' || v.state === 'cancelled') { clear(errorNote).appendChild(note(v.state === 'failed' ? 'bad' : 'warn', v.error || '')); errorNote.classList.remove('hidden'); }
  };
  render(view);
  const cancelBtn = btn('Cancelar', { onclick: async () => { if (finished) { d.close(final); return; } try { await api.post('/jobs/' + view.id + '/cancel'); } catch {} } });
  const bgBtn = btn('Seguir en segundo plano', { onclick: () => { background = true; d.close(null); } });
  const d = open({ iconName: 'download', title: view.title, subtitle: view.subtitle, dismissable: false, body: [msg, bar, steps, errorNote, note('info', 'Si algo falla, no queda nada a medias: ni una carpeta rota ni el zip sin verificar.')], footer: [bgBtn, cancelBtn] });
  if (!view.canCancel) cancelBtn.disabled = true;
  const done = waitJob(view.id, render).then(v => v, e => e.body || { state: 'failed', error: e.message, steps: view.steps });
  done.then(v => {
    finished = true; final = v;
    if (v.state === 'done') { if (!background) d.close(v); if (onDone) onDone(v); toast(v.message || 'Hecho'); }
    else { cancelBtn.textContent = 'Cerrar'; cancelBtn.disabled = false; bgBtn.classList.add('hidden'); if (background) toast(v.error || 'Falló', 'bad'); }
  });
  const result = await d.promise;
  if (result === null && !finished) return null;
  const v = await done;
  if (v.state !== 'done') throw new Error(v.error || 'Falló.');
  return v;
}

/** «Error de arranque»: por qué no arrancó, los puertos ocupados con su dueño y qué se puede hacer. */
export async function startError(failure, { retry, navigate, api, refresh }) {
  const body = [];
  body.push(h('div.sub', failure.wasRunning && failure.stillRunning ? 'Roost lo ha visto antes de parar nada: lo que estaba en marcha sigue en marcha, con la configuración de antes.' : failure.wasRunning ? 'Lo nuevo no arrancó y el stack ha quedado parado.' : 'No se ha podido arrancar. Abajo, el motivo y qué puedes hacer.'));
  for (const p of failure.busyPorts || []) {
    body.push(note('bad', h('div.strong', `El puerto ${p.port} (${p.role}) ya está en uso`), h('div', p.owner ? `Lo tiene ${p.owner}.` : 'No se sabe quién lo tiene.')));
  }
  if (!(failure.busyPorts || []).length) body.push(note('bad', failure.message));
  if (failure.logTail && failure.logTail.length) body.push(h('div', h('div.lbl', 'Final de los registros'), h('pre.log', failure.logTail.join('\n'))));
  const actions = h('div.col');
  for (const p of failure.busyPorts || []) {
    if (p.suggestion) actions.appendChild(h('div.row', btn(`Usar el puerto ${p.suggestion}`, { class: 'primary', onclick: async () => {
      try {
        if (p.key === '[db] port') await api.post('/db/port', { port: p.suggestion });
        else { const w = await api.get('/web'); await api.post('/web/ports', { http: p.key === '[web] port' ? p.suggestion : w.http.port, https: p.key === '[web] https_port' ? p.suggestion : w.https.port }); }
        d.close(true); refresh(); retry();
      } catch (e) { toast(e.message, 'bad'); }
    } }), h('span.hint', p.key === '[db] port' ? `La base de datos pasaría al ${p.suggestion} (está libre)` : p.key === '[web] https_port' ? `Los sitios pasarían a https://<sitio>.test:${p.suggestion} (el ${p.suggestion} está libre)` : `localhost pasaría al ${p.suggestion} (está libre)`)));
    actions.appendChild(h('div.row', btn('Elegir otro puerto…', { onclick: () => { d.close(true); navigate(p.key === '[db] port' ? 'bd' : 'web'); } }), h('span.hint', p.key === '[db] port' ? 'En Bases de datos' : 'En Servidor web')));
  }
  actions.appendChild(h('div.row', btn('Reintentar', { onclick: () => { d.close(true); retry(); } }), h('span.hint', 'Después de parar el programa que lo ocupa')));
  body.push(h('div', h('div.lbl', 'Qué puedes hacer'), actions));
  const d = open({ iconName: 'error', iconTone: 'bad', title: failure.title, wide: true, body, footer: [btn('Ver registros', { onclick: () => { d.close(true); navigate('registros'); } }), h('div.right', btn('Cerrar', { onclick: () => d.close(true) }))] });
  return d.promise;
}

/** «Confiar CA»: que el navegador confíe en los sitios .test. Devuelve true si se confió. */
export async function trustCa({ api, refresh }) {
  let ca;
  try { ca = await api.get('/ca'); } catch (e) { await message('La CA local', e.message, 'bad'); return false; }
  const isWin = (ca.store || '').includes('Windows');
  const body = [
    h('div', 'Roost firma los certificados de tus sitios con su propia autoridad (CA), que solo existe en este equipo. Para que el navegador se fíe, hay que añadirla a ', h('span.strong', isWin ? 'los certificados de confianza de tu usuario' : 'los almacenes de Chrome, Chromium, Edge y Firefox (NSS)'), '.'),
    h('div.kv', h('div.k', 'Autoridad'), h('div', ca.subject || 'Roost Local CA (se crea ahora)'), h('div.k', 'Huella'), h('div.mono', ca.thumbprint ? 'SHA-1 ' + ca.thumbprint.replace(/(..)(?=.)/g, '$1:') : 'se calcula al crearla'), h('div.k', 'Su clave'), h('div', h('span.mono', ca.keyFile), ' , legible solo por tu usuario')),
    isWin ? note('warn', 'Windows te enseñará a continuación un «Aviso de seguridad» preguntando si instalas este certificado (en el escritorio de este equipo). Comprueba que el nombre es ', h('span.strong', 'Roost Local CA'), ' y pulsa ', h('span.strong', 'Sí'), '.')
          : note('info', 'En Linux se usa certutil (NSS). Con «también el sistema» va además a /usr/local/share/ca-certificates (para curl y PHP), con permiso de administrador (pkexec o sudo).'),
  ];
  let system = false;
  if (!isWin) body.push(h('label.check', h('input', { type: 'checkbox', onchange: (e) => { system = e.target.checked; } }), h('span', 'También la CA del sistema (curl, PHP, wget)')));
  const d = open({ iconName: 'lock', title: 'Que el navegador confíe en tus sitios .test', subtitle: 'Una sola vez. Sin esto, el navegador avisa de que tus sitios https://*.test no son seguros.', body, footer: [btn('Ahora no', { onclick: () => d.close(false) }), h('div.right', btn('Continuar', { class: 'primary', onclick: async (e) => {
    e.target.disabled = true;
    try {
      const r = await api.post('/ca/trust', { system });
      d.close(r.trusted);
      if (r.trusted) toast('La CA de Roost ya es de confianza' + (r.stores && r.stores.length ? ' (' + r.stores.join('; ') + ')' : '') + (r.system ? ' · sistema: ' + r.system : ''));
      else await message('La CA no quedó como de confianza', (r.skipped && r.skipped.length ? r.skipped.join('; ') : 'Se canceló el aviso del sistema.') + (r.system ? ' ' + r.system : ''), 'warn');
      refresh();
    } catch (err) { e.target.disabled = false; await message('No se pudo', err.message, 'bad'); }
  } }))] });
  return d.promise;
}

/** «Bienvenida»: la primera vez, sin ningún PHP de Roost. */
export async function welcome({ api, navigate, refresh }) {
  const loading = open({ iconName: 'brand', title: 'Bienvenido a Roost', subtitle: 'Mirando qué hay en este equipo…', dismissable: false, body: [h('div.progress.indeterminate', h('div'))] });
  let w;
  try { w = await api.get('/welcome'); } catch (e) { loading.close(); await message('Bienvenida', e.message, 'bad'); return; }
  loading.close();
  if (!w.show) return;
  const found = h('div.card', w.found.length ? w.found.map(f => h('div.tr', h('div.row', h('span.strong', f.source), h('span.muted', f.components.join(', ')), f.chip ? h('span.right', chip(f.chip, f.red ? 'bad' : 'warn')) : null))) : h('div.card-b.muted', 'Nada de PHP, MySQL, MariaDB, nginx ni Apache fuera de Roost.'));
  found.querySelectorAll('.tr').forEach(tr => tr.style.gridTemplateColumns = '1fr');
  const phpCheck = h('input', { type: 'checkbox', checked: w.proposedPhp.length > 0, disabled: w.proposedPhp.length === 0 });
  const dbCheck = h('input', { type: 'checkbox', checked: !!w.db, disabled: !w.db });
  const phpLabel = w.proposedPhp.length ? `Instalar en Roost la última de cada rama de PHP que usas (${w.phpVersions.join(', ')})` : 'No hay catálogo de PHP (sin red): instala desde la pantalla PHP cuando la haya.';
  const body = [
    h('div.lbl', 'Encontrado en este equipo'), found,
    h('div.lbl', '¿Qué traemos?'),
    h('label.check', phpCheck, h('div', phpLabel, h('div.hint', w.proposedPhp.length ? `Y hacer global la ${w.proposedPhp[0]}. Lo de fuera sigue donde está.` : (w.catalogProblem || '')))),
    h('label.check', dbCheck, h('div', w.db ? `Una MariaDB LTS para los proyectos${w.dbPortBusy ? ` (el ${w.dbPort} está cogido: cámbialo en Bases de datos antes de iniciar)` : ''}` : 'Ya hay una base de datos en Roost')),
    h('div.form-grid', h('div.form-row', h('label', 'Carpeta de Roost'), h('div.field', shortPath(w.root, 44)), h('div.hint', 'Binarios, configuración y datos')), h('div.form-row', h('label', 'Proyectos nuevos'), h('div.field', shortPath(w.www, 44)), h('div.hint', 'Donde se crean los sitios nuevos'))),
    note('info', w.note),
  ];
  const d = open({ iconName: 'brand', title: 'Bienvenido a Roost', subtitle: w.found.length ? 'Ya tienes cosas instaladas. Roost se instala al lado: no mueve ni cambia nada.' : 'Roost instala PHP, el servidor web y la base de datos en su carpeta, sin tocar nada del sistema.', wide: true, body, footer: [btn('Saltar: lo hago luego', { class: 'link', onclick: () => d.close(false) }), h('div.right', btn('Empezar', { class: 'primary', onclick: async () => {
    const php = phpCheck.checked ? w.proposedPhp : [];
    const db = dbCheck.checked && w.db ? w.db : null;
    if (!php.length && !db) { d.close(false); return; }
    d.close(true);
    try { await job(() => api.post('/welcome/run', { php, db }), { api }); refresh(); navigate('inicio'); }
    catch (e) { /* ya se enseñó */ }
  } }))] });
  return d.promise;
}
