const $ = s => document.querySelector(s);

// Si ya hay una sesión válida, no se vuelve a pedir la contraseña.
fetch('/api/yo').then(r => { if (r.ok) location.replace('/'); }).catch(() => {});

$('#f-login').addEventListener('submit', async e => {
  e.preventDefault();
  const usuario = $('#l-usuario').value.trim();
  const clave = $('#l-clave').value;
  const msg = $('#l-msg');
  msg.textContent = '';
  msg.className = '';
  if (!usuario || !clave) { mostrar('Ingrese su usuario y su contraseña.'); return; }

  $('#l-entrar').disabled = true;
  try {
    const r = await fetch('/api/login', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ usuario, clave })
    });
    if (r.ok) { location.replace('/'); return; }
    const cuerpo = await r.json().catch(() => ({}));
    $('#l-clave').value = '';
    mostrar(cuerpo.error || 'No se pudo iniciar sesión. Intente nuevamente.');
  } catch {
    mostrar('No se pudo conectar con el servidor.');
  } finally {
    $('#l-entrar').disabled = false;
  }
});

// textContent evita interpretar como HTML cualquier texto que venga del servidor.
function mostrar(texto) {
  const msg = $('#l-msg');
  msg.className = 'msg err';
  msg.textContent = texto;
}
