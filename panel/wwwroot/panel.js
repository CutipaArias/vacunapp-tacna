const $ = s => document.querySelector(s);
const fmt = n => n == null ? '—' : Number(n).toLocaleString('es-PE');
const pct = n => n == null ? '—' : Number(n).toFixed(1) + ' %';
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fecha = s => s ? String(s).slice(0, 10) : '—';
let cat, yo;

async function api(url, opts) {
  const t0 = performance.now();
  const r = await fetch(url, opts);
  if (r.status === 401) { location.replace('/login.html'); throw new Error('Sesión no iniciada.'); }
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(body.error || r.statusText);
  body.__ms = Math.round(performance.now() - t0);
  return body;
}
function table(el, cols, rows) {
  el.innerHTML = '<thead><tr>' + cols.map(c => `<th class="${c.n ? 'n' : ''}">${c.h}</th>`).join('') + '</tr></thead><tbody>' +
    (rows.length ? rows.map(r => '<tr>' + cols.map(c => `<td class="${c.n ? 'n' : ''}">${c.f ? c.f(r) : esc(r[c.k])}</td>`).join('') + '</tr>').join('')
                 : `<tr><td colspan="${cols.length}" class="muted">Sin resultados</td></tr>`) + '</tbody>';
}
const bar = v => `<span class="bar"><i style="width:${Math.min(v ?? 0, 100)}%"></i><em></em></span>${pct(v)}`;
const tag = v => `<span class="tag ${esc(v)}">${esc(v)}</span>`;

document.querySelectorAll('nav button').forEach(b => b.onclick = () => {
  document.querySelectorAll('nav button, section').forEach(x => x.classList.remove('on'));
  b.classList.add('on'); $('#' + b.dataset.tab).classList.add('on');
  ({ cobertura: cargarCobertura, alertas: cargarAlertas, campanas: cargarCampanas })[b.dataset.tab]?.();
});

async function cargarResumen() {
  const k = await api('/api/resumen');
  $('#kpis').innerHTML = [
    ['Pacientes registrados', fmt(k.TotalPacientes)], ['Dosis aplicadas', fmt(k.TotalDosis)],
    ['Cobertura SPR1 regional', pct(k.CoberturaRegionalSPR1)], ['Cobertura SPR2 regional', pct(k.CoberturaRegionalSPR2)],
    ['Brotes activos', `${fmt(k.BrotesActivos)} <small class="muted">(${fmt(k.CasosActivos)} casos)</small>`],
    ['Alertas pendientes', fmt(k.AlertasPendientes)]
  ].map(([t, v]) => `<div class="kpi"><span>${t}</span><b>${v}</b></div>`).join('');
  const s = await api('/api/sarampion');
  $('#t-sar').textContent = `· ${s.__ms} ms`;
  table($('#sar'), [
    { h: 'Distrito', f: r => esc(r.Distrito) + (r.BroteActivo ? ` <span class="tag ALTO">brote · ${r.CasosConfirmados} casos</span>` : '') },
    { h: 'Provincia', k: 'Provincia' },
    { h: 'Elegibles', k: 'ElegiblesSPR1', n: 1, f: r => fmt(r.ElegiblesSPR1) },
    { h: 'SPR 1.a dosis', f: r => bar(r.CoberturaSPR1) },
    { h: 'SPR 2.a dosis', f: r => bar(r.CoberturaSPR2) },
    { h: 'Riesgo', f: r => tag(r.NivelRiesgo) }
  ], s);
}

async function cargarCobertura() {
  const q = new URLSearchParams({ vacuna: $('#f-vac').value, dosis: $('#f-dos').value, provincia: $('#f-prov').value });
  const r = await api('/api/cobertura?' + q);
  $('#t-cob').textContent = `· ${r.length} filas · ${r.__ms} ms`;
  table($('#cob'), [
    { h: 'Distrito', k: 'Distrito' }, { h: 'Vacuna', k: 'CodigoVacuna' }, { h: 'Dosis', k: 'Dosis' },
    { h: 'Elegibles', n: 1, f: r => fmt(r.Elegibles) }, { h: 'Vacunados', n: 1, f: r => fmt(r.Vacunados) },
    { h: 'Sin vacunar', n: 1, f: r => fmt(r.SinVacunar) },
    { h: 'Cobertura', f: r => bar(r.PorcentajeCobertura) }, { h: 'Clasificación', f: r => tag(r.Clasificacion) }
  ], r);
}

async function cargarAlertas() {
  const r = await api('/api/alertas?' + new URLSearchParams({ ubigeo: $('#a-dis').value, top: 200 }));
  $('#t-ale').textContent = `· primeras ${r.length} · ${r.__ms} ms`;
  table($('#ale'), [
    { h: 'Tipo', f: r => tag(r.TipoAlerta) }, { h: 'DNI', k: 'NumeroDocumento' }, { h: 'Paciente', k: 'Paciente' },
    { h: 'Edad (m)', n: 1, k: 'EdadMeses' }, { h: 'Distrito', k: 'Distrito' },
    { h: 'Dosis', f: r => `${esc(r.CodigoVacuna)} · ${esc(r.Dosis)}` }, { h: 'Teléfono', k: 'Telefono' },
    { h: 'Días abierta', n: 1, k: 'DiasAbierta' }
  ], r);
}

async function cargarCampanas() {
  const r = await api('/api/campanas');
  table($('#cam'), [
    { h: 'Campaña', k: 'Campana' }, { h: 'Periodo', f: r => `${fecha(r.FechaInicio)} → ${fecha(r.FechaFin)}` },
    { h: 'Distrito', k: 'Distrito' }, { h: 'Meta', n: 1, f: r => fmt(r.MetaDosis) },
    { h: 'Aplicadas', n: 1, f: r => fmt(r.DosisAplicadas) }, { h: 'Avance', f: r => bar(r.PorcentajeAvance) }
  ], r);
}

async function buscarPaciente() {
  const doc = $('#p-doc').value.trim();
  if (!doc) return;
  $('#reg').hidden = true;
  try {
    const p = await api('/api/paciente/' + encodeURIComponent(doc));
    const d = p.datos;
    $('#pac').innerHTML = `<p><b>${esc(d.Paciente)}</b> · ${esc(d.TipoDocumento)} ${esc(d.NumeroDocumento)} · ${d.EdadMeses} meses ·
      ${esc(d.Distrito)} · ${esc(d.Direccion)} · ${esc(d.Telefono)}</p><div class="grid2"><div class="tbl"><table id="p-apl"></table></div><div class="tbl"><table id="p-pen"></table></div></div>`;
    table($('#p-apl'), [{ h: 'Aplicada', f: r => `${esc(r.CodigoVacuna)} · ${esc(r.Dosis)}` }, { h: 'Fecha', f: r => fecha(r.FechaAplicacion) },
      { h: 'Lote', k: 'NumeroLote' }, { h: 'Establecimiento', k: 'Establecimiento' }], p.aplicadas);
    table($('#p-pen'), [{ h: 'Pendiente', f: r => `${esc(r.CodigoVacuna)} · ${esc(r.Dosis)}` }, { h: 'Meses de atraso', n: 1, k: 'MesesAtraso' }], p.pendientes);
    prepararRegistro(doc, p.pendientes);
  } catch (e) { $('#pac').innerHTML = `<div class="msg err">${esc(e.message)}</div>`; }
}

function prepararRegistro(doc, pendientes) {
  if (yo.rol !== 'VACUNADOR' || !pendientes.length) return;   // solo el vacunador registra dosis
  $('#reg').hidden = false; $('#r-msg').innerHTML = '';
  $('#r-dosis').innerHTML = pendientes.map(p => `<option value="${esc(p.CodigoVacuna)}|${p.NumeroDosis}">${esc(p.CodigoVacuna)} · ${esc(p.Dosis)}</option>`).join('');
  const lotes = () => {
    const v = $('#r-dosis').value.split('|')[0];
    $('#r-lote').innerHTML = cat.lotes.filter(l => l.Codigo === v).map(l => `<option>${esc(l.NumeroLote)}</option>`).join('');
  };
  const personal = () => {
    $('#r-per').innerHTML = cat.personal.filter(p => p.IdEstablecimiento == $('#r-est').value).map(p => `<option value="${p.Dni}">${esc(p.Nombre)}</option>`).join('');
  };
  $('#r-dosis').onchange = lotes; $('#r-est').onchange = personal; lotes(); personal();
  $('#r-fec').value = new Date().toISOString().slice(0, 10);
  $('#b-reg').onclick = async () => {
    const [vacuna, dosis] = $('#r-dosis').value.split('|');
    try {
      const r = await api('/api/dosis', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
        documento: doc, vacuna, dosis: +dosis, lote: $('#r-lote').value, idEstablecimiento: +$('#r-est').value,
        dniVacunador: $('#r-per').value, fecha: $('#r-fec').value || null }) });
      await buscarPaciente();
      $('#r-msg').innerHTML = `<div class="msg ok">Dosis registrada (Id ${r.idDosis}). Las alertas asociadas se cerraron automáticamente.</div>`;
    } catch (e) { $('#r-msg').innerHTML = `<div class="msg err">${esc(e.message)}</div>`; }
  };
}

const ROLES = {
  ADMINISTRADOR: 'Administrador', EPIDEMIOLOGO: 'Epidemiólogo', JEFE_ESTABLECIMIENTO: 'Jefe de establecimiento',
  VACUNADOR: 'Vacunador', CIUDADANO: 'Ciudadano'
};

async function salir() {
  await fetch('/api/logout', { method: 'POST' }).catch(() => {});
  location.replace('/login.html');
}

// El menú solo muestra lo que el rol puede usar; el servidor vuelve a comprobarlo en cada solicitud.
function armarMenu(rol) {
  const permitidos = [...document.querySelectorAll('nav button')].filter(b => b.dataset.roles.split(' ').includes(rol));
  document.querySelectorAll('nav button').forEach(b => b.hidden = !permitidos.includes(b));
  return permitidos.map(b => b.dataset.tab);
}

async function init() {
  yo = await api('/api/yo');
  $('#quien').textContent = `${yo.usuario} · ${ROLES[yo.rol] ?? yo.rol}`;
  $('#sesion').hidden = false;
  $('#b-salir').onclick = salir;

  const tabs = armarMenu(yo.rol);
  if (!tabs.length) {
    $('#aviso').innerHTML = '<div class="msg ok">Su cuenta no tiene opciones habilitadas todavía.</div>';
    return;
  }

  cat = await api('/api/catalogos');
  $('#f-vac').innerHTML += [...new Set(cat.esquema.map(e => e.Codigo))].map(c => `<option>${esc(c)}</option>`).join('');
  $('#a-dis').innerHTML += cat.distritos.map(d => `<option value="${esc(d.Ubigeo)}">${esc(d.Nombre)}</option>`).join('');
  $('#r-est').innerHTML = cat.establecimientos
    .filter(e => yo.idEstablecimiento == null || e.IdEstablecimiento === yo.idEstablecimiento)
    .map(e => `<option value="${e.IdEstablecimiento}">${esc(e.Nombre)} (${esc(e.Distrito)})</option>`).join('');
  $('#b-cob').onclick = cargarCobertura; $('#b-ale').onclick = cargarAlertas; $('#b-pac').onclick = buscarPaciente;
  $('#p-doc').onkeydown = e => e.key === 'Enter' && buscarPaciente();

  document.querySelector(`nav button[data-tab="${tabs[0]}"]`).click();
  if (tabs[0] === 'resumen') await cargarResumen();
}
init().catch(e => { if (e.message !== 'Sesión no iniciada.') $('#aviso').innerHTML = `<div class="msg err">No se pudo cargar la aplicación: ${esc(e.message)}</div>`; });
