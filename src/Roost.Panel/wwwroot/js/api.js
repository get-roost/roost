// La API del panel: JSON bajo /api, siempre con la cookie de la sesión (la pone el navegador) y, en lo
// que escribe, la cabecera anti-CSRF. Un 401 es una sesión caducada (el panel se reinició): se dice y
// se para todo. Un error de Roost llega como {error} con el mismo texto que la CLI y la app.

export class ApiError extends Error {
  constructor(message, status, body) { super(message); this.status = status; this.body = body; }
}

let onUnauthorized = () => {};
export function setUnauthorizedHandler(fn) { onUnauthorized = fn; }

async function request(method, path, body) {
  const headers = { 'Accept': 'application/json' };
  if (method !== 'GET') { headers['X-Roost-Panel'] = '1'; headers['Content-Type'] = 'application/json'; }
  let response;
  try {
    response = await fetch('/api' + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), credentials: 'same-origin', cache: 'no-store' });
  } catch (e) {
    throw new ApiError('No se llega al panel (¿se ha parado? roost panel status).', 0, null);
  }
  if (response.status === 401) { onUnauthorized(); throw new ApiError('La sesión del panel ya no vale.', 401, null); }
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = { raw: text }; }
  if (!response.ok) throw new ApiError((data && data.error) || `Error ${response.status}`, response.status, data);
  return data;
}

export const api = {
  get: (path) => request('GET', path),
  post: (path, body) => request('POST', path, body ?? {}),
  del: (path) => request('DELETE', path),
  /** Descarga un fichero (exportar): abre la URL en otra pestaña del mismo origen. */
  download: (path) => window.open('/api' + path, '_blank'),
};

/**
 * Sondea una tarea larga hasta que acabe. onUpdate(view) con cada estado; devuelve la vista final.
 * Lanza ApiError si la tarea falla o se cancela.
 */
export async function waitJob(id, onUpdate, interval = 400) {
  for (;;) {
    const view = await api.get('/jobs/' + id);
    if (onUpdate) onUpdate(view);
    if (view.state !== 'running') {
      if (view.state === 'done') return view;
      throw new ApiError(view.error || (view.state === 'cancelled' ? 'Cancelado.' : 'Falló.'), 0, view);
    }
    await new Promise(r => setTimeout(r, interval));
  }
}
