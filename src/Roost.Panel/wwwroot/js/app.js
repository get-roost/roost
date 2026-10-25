// El armazón del panel: la barra de título, la lateral, la de estado y los diálogos sobre la ventana,
// como MainWindow de la app; sondea el estado cada 3 s (MainViewModel) y navega entre las pantallas.
import { api, setUnauthorizedHandler, ApiError } from './api.js';
import { h, icon, chip, dot, clear, toast, shortPath, plural } from './ui.js';
import * as dialogs from './dialogs.js';
import * as home from './pages/home.js';
import * as sites from './pages/sites.js';
import * as php from './pages/php.js';
import * as web from './pages/web.js';
import * as db from './pages/db.js';
import * as audit from './pages/audit.js';
import * as logs from './pages/logs.js';
import * as settings from './pages/settings.js';
import * as news from './pages/news.js';
import * as soon from './pages/soon.js';

const PAGES = {
  inicio: { label: 'Inicio', icon: 'home', mod: home },
  sitios: { label: 'Sitios', icon: 'globe', mod: sites },
  php: { label: 'PHP', icon: 'code', mod: php, group: 'COMPONENTES' },
  web: { label: 'Servidor web', icon: 'server', mod: web, group: 'COMPONENTES' },
  bd: { label: 'Bases de datos', icon: 'database', mod: db, group: 'COMPONENTES' },
  redis: { label: 'Redis', icon: 'layers', mod: soon, group: 'COMPONENTES', soon: true },
  correo: { label: 'Correo', icon: 'mail', mod: soon, group: 'COMPONENTES', soon: true },
  auditoria: { label: 'Auditoría', icon: 'shieldCheck', mod: audit, group: 'ROOST' },
  registros: { label: 'Registros', icon: 'file', mod: logs, group: 'ROOST' },
  ajustes: { label: 'Ajustes', icon: 'sliders', mod: settings, group: 'ROOST', tip: 'Ctrl+,' },
  novedades: { label: 'Novedades', icon: 'gift', mod: news, group: 'ROOST' },
};

export const state = {
  status: null,        // lo último de GET /api/status
  page: 'inicio',
  params: {},
  busy: false,         // arrancando, parando o aplicando
  busyText: null,
  redCount: 0,
  listeners: new Set(),
};

export function onStatus(fn) { state.listeners.add(fn); return () => state.listeners.delete(fn); }

// Tema: como el sistema, claro u oscuro (la preferencia de este navegador).
export function applyTheme(theme) {
  try { localStorage.setItem('roost-theme', theme); } catch {}
  if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
  else delete document.documentElement.dataset.theme;
}
export function currentTheme() { try { return localStorage.getItem('roost-theme') || 'system'; } catch { return 'system'; } }
applyTheme(currentTheme());

const $ = (id) => document.getElementById(id);

// Navegación

export function navigate(page, params = {}) {
  if (!PAGES[page]) page = 'inicio';
  state.page = page;
  state.params = params;
  const hash = '#/' + page + (Object.keys(params).length ? '?' + new URLSearchParams(params) : '');
  if (location.hash !== hash) history.replaceState(null, '', hash);
  renderNav();
  renderPage();
}

function fromHash() {
  const m = /^#\/([a-z]+)(?:\?(.*))?$/.exec(location.hash || '');
  const page = m ? m[1] : 'inicio';
  const params = m && m[2] ? Object.fromEntries(new URLSearchParams(m[2])) : {};
  return { page, params };
}

let pageToken = 0;
export async function renderPage() {
  const token = ++pageToken;
  const content = $('content');
  clear(content);
  const def = PAGES[state.page];
  const ctx = { state, navigate, dialogs, refresh: () => renderPage(), api, flash, startStack, stopStack, pageDef: def, params: state.params, stale: () => token !== pageToken };
  try {
    await def.mod.render(content, ctx);
  } catch (e) {
    if (token !== pageToken) return;
    content.appendChild(h('div.note.bad', icon('error'), h('div', e.message || String(e))));
  }
  // ?dialog=…: abre un diálogo al cargar (las capturas de los tests y los enlaces directos).
  if (token === pageToken && state.params.dialog && !dialogs.isOpen()) {
    const which = state.params.dialog;
    if (which === 'nuevo-sitio') sites.newSite(ctx);
    else if (which === 'confiar-ca') dialogs.trustCa({ api, refresh: () => renderPage() });
    else if (which === 'bienvenida') dialogs.welcome({ api, navigate, refresh: () => renderPage() });
    else if (which === 'error-arranque') dialogs.startError({ title: 'No se ha aplicado el cambio', message: 'El puerto 8445 (HTTPS de los sitios .test) ya esta en uso', busyPorts: [{ port: 8445, role: 'HTTPS de los sitios .test', key: '[web] https_port', suggestion: 9443, owner: 'powershell (PID 4242)' }], wasRunning: true, stillRunning: true, logTail: ['Sigue en marcha lo de antes, con la configuracion anterior.'] }, { retry: () => {}, navigate, api, refresh: () => renderPage() });
    else if (which === 'instalando') dialogs.job(() => Promise.resolve({ job: { id: 'demo', title: 'Instalando Xdebug 3.5.3', subtitle: 'Para PHP 8.4 · NTS · x64 · desde xdebug.org', state: 'running', message: 'Descargando', progress: 0.35, canCancel: true, steps: [{ label: 'Xdebug 3.5.3 para PHP 8.4', detail: 'php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll', state: 'done' }, { label: 'Descargando de xdebug.org', state: 'active' }, { label: 'Verificar el SHA-256', detail: 'fijado en Roost, antes de usarla', state: 'pending' }, { label: 'Dejarla en su carpeta', detail: 'bin/xdebug/3.5.3', state: 'pending' }] } }), { api: { post: async () => ({}) } }).catch(() => {});
  }
}

function renderNav() {
  const nav = clear($('nav'));
  let group = null;
  for (const [key, def] of Object.entries(PAGES)) {
    if (def.group && def.group !== group) { group = def.group; nav.appendChild(h('div.navlabel', group)); }
    const b = h('button.nav' + (state.page === key ? '.on' : '') + (def.soon ? '.soon' : ''), { title: def.tip || def.label, onclick: () => navigate(key), 'aria-label': def.label },
      icon(def.icon, 16), h('span.label', def.label));
    if (key === 'auditoria' && state.redCount > 0) b.appendChild(h('span.count', String(state.redCount)));
    if (def.soon) b.appendChild(h('span.soon-tag', 'pronto'));
    nav.appendChild(b);
  }
}

// Estado

let lastJson = '';
export async function refreshStatus(force = false) {
  let status;
  try { status = await api.get('/status'); }
  catch (e) { if (e.status === 401) return; status = null; }
  const json = JSON.stringify(status);
  if (!force && json === lastJson) return;
  lastJson = json;
  state.status = status;
  renderShell();
  for (const fn of state.listeners) { try { fn(status); } catch (e) { console.error(e); } }
}

function runInfo() {
  const d = state.status && state.status.daemon;
  if (state.busy) return { text: state.busyText || 'Trabajando…', tone: 'warn' };
  if (!d) return { text: 'Parado', tone: '' };
  if (!d.running) return { text: 'Parado', tone: '' };
  const failed = d.services.some(s => s.state === 'Failed');
  const restarting = d.services.some(s => s.state === 'Restarting');
  return { text: failed ? 'Con fallos' : restarting ? 'Reiniciando' : 'En marcha', tone: failed ? 'bad' : restarting ? 'warn' : 'ok' };
}

function renderShell() {
  const s = state.status;
  const d = s && s.daemon;
  const run = runInfo();
  const runChip = $('run-chip'); clear(runChip); runChip.className = 'chip ' + run.tone; runChip.append(dot(run.tone), h('span', run.text));
  $('foot-dot').className = 'dot ' + run.tone;
  const services = d && d.running ? d.services.length : 0;
  $('foot-text').textContent = d && d.running ? `${run.text} · ${plural(services, 'servicio', 'servicios')}` : run.text;
  const toggle = $('toggle');
  const running = !!(d && d.running);
  toggle.className = 'bigtoggle ' + (running ? 'stop' : 'start');
  toggle.disabled = state.busy || !s;
  clear(toggle).append(icon(running ? 'stop' : 'play', 14, 2, true), h('span.label', running ? 'Parar todo' : 'Iniciar todo'));
  toggle.onclick = () => running ? stopStack() : startStack();
  $('sb-dot').className = 'dot ' + run.tone;
  $('sb-run').textContent = run.text;
  $('sb-daemon').textContent = d ? `roostd · PID ${d.daemonPid}` : 'roostd parado';
  $('sb-ca').textContent = !s ? '' : !s.ca.created ? 'CA local: aún no creada' : s.ca.trusted ? 'CA local: de confianza' : 'CA local: sin confiar';
  $('sb-hosts').textContent = !s ? '' : s.hosts.mode === 'dns' ? 'nombres .test: DNS local' : s.hosts.inSync ? 'hosts: al día' : 'hosts: sin sincronizar';
  const root = $('sb-root'); clear(root); if (s) root.appendChild(h('span', { title: s.root }, 'Roost · ' + s.root));
  $('panel-url').textContent = s ? `${s.os === 'windows' ? 'Windows' : 'Linux'} · panel web` : '';
}

let flashTimer = null;
export function flash(text) {
  const el = $('sb-flash');
  el.textContent = text; el.classList.remove('hidden'); $('sb-root').classList.add('hidden');
  clearTimeout(flashTimer);
  flashTimer = setTimeout(() => { el.classList.add('hidden'); $('sb-root').classList.remove('hidden'); }, 4000);
}

// Arrancar y parar (lo que en la app hace StackController)

export async function startStack(opts = {}) {
  if (state.busy) return false;
  setBusy(true, state.status && state.status.daemon && state.status.daemon.running ? 'Aplicando cambios…' : 'Arrancando…');
  try {
    const r = await api.post('/stack/up');
    await refreshStatus(true);
    if (r.ok) {
      flash(r.wasRunning ? (r.applied && r.applied.length ? 'Aplicado: ' + r.applied.join('; ') : 'Sin cambios') : 'En marcha');
      return true;
    }
    await dialogs.startError(r.failure, { retry: () => startStack(opts), navigate, api, refresh: () => { refreshStatus(true); renderPage(); } });
    return false;
  } catch (e) {
    await dialogs.message('No se pudo arrancar Roost', e.message, 'bad');
    return false;
  } finally {
    setBusy(false);
    await refreshStatus(true);
    renderPage();
  }
}

export async function stopStack() {
  if (state.busy) return;
  setBusy(true, 'Parando…');
  try { await api.post('/stack/down'); flash('Parado'); }
  catch (e) { await dialogs.message('No se pudo parar', e.message, 'bad'); }
  finally { setBusy(false); await refreshStatus(true); renderPage(); }
}

function setBusy(busy, text) { state.busy = busy; state.busyText = text || null; renderShell(); }

// Sesión caducada: el panel se reinició (token nuevo) o se paró.
setUnauthorizedHandler(() => {
  const content = clear($('content'));
  content.appendChild(h('div.card', h('div.card-b.stack',
    h('p.h3', 'La sesión del panel ya no vale'),
    h('p.muted', 'El panel se ha reiniciado (el código de acceso es nuevo en cada arranque) o se ha parado. Vuelve a abrirlo con la dirección que da ', h('code', 'roost panel url'), '.'))));
});

// Atajos (los de la app): Ctrl+N nuevo sitio, F5 actualizar, Ctrl+L registros, Ctrl+, ajustes.
document.addEventListener('keydown', (e) => {
  if (dialogs.isOpen()) { if (e.key === 'Escape') dialogs.dismiss(); return; }
  if (e.key === 'F5') { e.preventDefault(); refreshStatus(true); renderPage(); }
  else if (e.ctrlKey && e.key.toLowerCase() === 'n') { e.preventDefault(); sites.newSite({ state, navigate, dialogs, api, flash, refresh: () => renderPage() }); }
  else if (e.ctrlKey && e.key.toLowerCase() === 'l') { e.preventDefault(); navigate('registros'); }
  else if (e.ctrlKey && e.key === ',') { e.preventDefault(); navigate('ajustes'); }
});

window.addEventListener('hashchange', () => { const { page, params } = fromHash(); if (page !== state.page || JSON.stringify(params) !== JSON.stringify(state.params)) navigate(page, params); });

// Arranque
$('brand').appendChild(icon('brand', 14, 2.2));
(async () => {
  await refreshStatus(true);
  const { page, params } = fromHash();
  navigate(page, params);
  setInterval(() => refreshStatus(), 3000);
  // La bienvenida: sin ningún PHP de Roost, una vez por sesión del navegador.
  if (state.status && state.status.welcome && !sessionStorage.getItem('roost-welcome-shown')) {
    sessionStorage.setItem('roost-welcome-shown', '1');
    dialogs.welcome({ api, navigate, refresh: () => { refreshStatus(true); renderPage(); } });
  }
})();
