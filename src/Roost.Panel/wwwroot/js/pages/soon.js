// Lo que en el panel está «próximamente» (Redis y Correo): se ve y se explica, pero sin acciones.
import { h, icon, note, btn } from '../ui.js';

export async function render(root, ctx) {
  const isRedis = ctx.state.page === 'redis';
  root.appendChild(h('div.page-head', h('div.titles', h('h1.h1', isRedis ? 'Redis' : 'Correo'), h('div.sub', isRedis ? 'Redis en el stack (caché, colas, sesiones), solo en 127.0.0.1.' : 'Mailpit en el stack: el correo de PHP se queda en una bandeja local en vez de salir del equipo.'))));
  root.appendChild(h('div.stack',
    note('info', h('div', h('div.strong', 'Próximamente en el panel web'), h('div', isRedis
      ? 'Desde la terminal ya va: roost redis install | enable | disable | port <puerto> | status. En Linux llega en la próxima versión.'
      : 'Desde la terminal ya va: roost mail install | enable | disable | port --smtp <p> --web <p> | open | status. En Linux llega en la próxima versión.'))),
    h('div.card', h('div.card-h', h('span.h2', 'Qué hará aquí')), h('div.card-b', h('ul', { class: 'muted' },
      h('li', isRedis ? 'Instalar Redis (la build MSYS2 de redis-windows, verificada) y ponerlo en el stack.' : 'Instalar Mailpit (verificado) y ponerlo en el stack.'),
      h('li', 'Cambiar el puerto, ver si contesta' + (isRedis ? ' (PING)' : '') + ' y dónde están su configuración y sus datos.'),
      h('li', isRedis ? 'Las líneas para tu .env (REDIS_HOST, REDIS_PORT).' : 'Abrir la bandeja y las líneas para tu .env (MAIL_HOST, MAIL_PORT).')))),
    h('div.row', btn('Volver a Inicio', { onclick: () => ctx.navigate('inicio') }))));
}
