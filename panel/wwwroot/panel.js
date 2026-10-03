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
  ({ cobertura: cargarCobertura, alertas: cargarAlertas, campanas: cargarCampanas, citas: cargarCitas, stock: cargarStock, horarios: cargarHorarios, usuarios: cargarUsuarios, auditoria: cargarAuditoria })[b.dataset.tab]?.();
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
  $('#r-dosis').onchange = lotes; lotes();
  $('#r-fec').value = new Date().toISOString().slice(0, 10);
  $('#b-reg').onclick = async () => {
    const [vacuna, dosis] = $('#r-dosis').value.split('|');
    try {
      const r = await api('/api/dosis', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
        documento: doc, vacuna, dosis: +dosis, lote: $('#r-lote').value, idEstablecimiento: +$('#r-est').value,
        fecha: $('#r-fec').value || null }) });
      await buscarPaciente();
      $('#r-msg').innerHTML = `<div class="msg ok">Dosis registrada (Id ${r.idDosis}). Las alertas asociadas se cerraron automáticamente.</div>`;
    } catch (e) { $('#r-msg').innerHTML = `<div class="msg err">${esc(e.message)}</div>`; }
  };
}

// ---- Stock del establecimiento (solo JEFE_ESTABLECIMIENTO) ----
const json = cuerpo => ({ method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(cuerpo) });
const TIPOS_ALERTA = { STOCK_BAJO: 'Stock bajo', LOTE_POR_VENCER: 'Lote por vencer' };
let stock = [];

async function cargarStock() {
  const [alertas, filas] = await Promise.all([api('/api/stock/alertas'), api('/api/stock')]);   // alertas primero: calcula los vencimientos
  stock = filas;
  $('#t-stk-ale').textContent = `· ${alertas.length} pendientes`;
  table($('#stk-ale'), [
    { h: 'Alerta', f: r => esc(TIPOS_ALERTA[r.tipoAlerta] ?? r.tipoAlerta) }, { h: 'Vacuna', k: 'vacuna' }, { h: 'Lote', k: 'numeroLote' },
    { h: 'Vence', f: r => fecha(r.fechaVencimiento) }, { h: 'Existencias', n: 1, f: r => fmt(r.cantidad) }, { h: 'Umbral', n: 1, f: r => fmt(r.umbralMinimo) }
  ], alertas);
  $('#t-stk').textContent = `· ${filas.length} lotes · ${filas.__ms} ms`;
  table($('#stk-tbl'), [
    { h: 'Vacuna', k: 'vacuna' }, { h: 'Lote', k: 'numeroLote' }, { h: 'Laboratorio', k: 'laboratorio' },
    { h: 'Vence', f: r => fecha(r.fechaVencimiento) }, { h: 'Existencias', n: 1, f: r => fmt(r.cantidad) }, { h: 'Umbral', n: 1, f: r => fmt(r.umbralMinimo) },
    { h: 'Estado', f: r => r.bajoUmbral ? '<span class="tag CRÍTICA">Bajo</span>' : '<span class="tag ÓPTIMA">Normal</span>' },
    { h: '', f: r => `<button class="b sec" data-id="${r.idStock}">Ajustar</button>` }
  ], filas);
}

function prepararStock() {
  $('#i-vac').innerHTML = [...new Set(cat.esquema.map(e => e.Codigo))].map(c => `<option>${esc(c)}</option>`).join('');
  $('#f-ing').onsubmit = async e => {
    e.preventDefault();
    try {
      await api('/api/stock/ingresos', json({
        vacuna: $('#i-vac').value, numeroLote: $('#i-lote').value.trim(), laboratorio: $('#i-lab').value.trim(),
        fechaVencimiento: $('#i-ven').value, cantidad: +$('#i-cant').value, umbralMinimo: $('#i-umb').value === '' ? null : +$('#i-umb').value }));
      $('#i-msg').innerHTML = '<div class="msg ok">Lote ingresado.</div>';
      $('#f-ing').reset();
      await cargarStock();
    } catch (err) { $('#i-msg').innerHTML = `<div class="msg err">${esc(err.message)}</div>`; }
  };
  let ajustando = null;
  $('#stk-tbl').onclick = e => {
    const b = e.target.closest('button[data-id]');
    if (!b) return;
    ajustando = stock.find(x => x.idStock === +b.dataset.id);
    $('#aj-lote').textContent = `${ajustando.vacuna} · lote ${ajustando.numeroLote} (hay ${fmt(ajustando.cantidad)})`;
    $('#aj-cant').value = ajustando.cantidad; $('#aj-mot').value = '';
    $('#f-aj').hidden = false; $('#aj-msg').innerHTML = ''; $('#aj-cant').focus();
  };
  $('#aj-cancelar').onclick = () => { $('#f-aj').hidden = true; ajustando = null; };
  $('#f-aj').onsubmit = async e => {
    e.preventDefault();
    try {
      await api('/api/stock/ajustes', json({ idStock: ajustando.idStock, cantidadNueva: +$('#aj-cant').value, motivo: $('#aj-mot').value.trim() }));
      $('#f-aj').hidden = true; ajustando = null;
      $('#aj-msg').innerHTML = '<div class="msg ok">Existencia ajustada.</div>';
      await cargarStock();
    } catch (err) { $('#aj-msg').innerHTML = `<div class="msg err">${esc(err.message)}</div>`; }
  };
}

// ---- Reservar cita: paciente → dosis → franja → confirmar (ciudadano, vacunador y jefe) ----
let citaDoc = null, citaPendientes = [], citasPaciente = [], citaEditando = null;
const diaHora = s => new Date(s).toLocaleString('es-PE', { weekday: 'short', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });

async function cargarCitas() {
  if (yo.rol === 'CIUDADANO') {
    const hijos = await api('/api/mis-pacientes');
    $('#c-hijo').innerHTML = hijos.length
      ? hijos.map(h => `<option value="${esc(h.numeroDocumento)}">${esc(h.nombre)} (${esc(h.parentesco)})</option>`).join('')
      : '<option value="">Sin hijos vinculados</option>';
    $('#c-hijos').hidden = false;
    await elegirPacienteCita($('#c-hijo').value);
  }
}

async function elegirPacienteCita(doc) {
  citaDoc = null; citaPendientes = [];
  ['#p-dosis', '#p-franja', '#p-confirmar'].forEach(s => $(s).hidden = true);
  $('#c-msg').innerHTML = ''; $('#c-paciente').textContent = ''; table($('#c-tbl'), [], []);
  if (!doc) return;
  try {
    const p = await api('/api/paciente/' + encodeURIComponent(doc));
    citaDoc = doc; citaPendientes = p.pendientes;
    $('#c-paciente').innerHTML = `<b>${esc(p.datos.Paciente)}</b> · ${esc(p.datos.TipoDocumento)} ${esc(p.datos.NumeroDocumento)} · ${p.datos.EdadMeses} meses`;
    await cargarCitasPaciente();
    if (!citaPendientes.length) { $('#c-msg').innerHTML = '<div class="msg ok">Este paciente no tiene dosis pendientes.</div>'; return; }
    $('#c-dosis').innerHTML = citaPendientes.map(x => `<option value="${esc(x.CodigoVacuna)}|${x.NumeroDosis}">${esc(x.CodigoVacuna)} · ${esc(x.Dosis)}</option>`).join('');
    $('#p-dosis').hidden = false; $('#p-franja').hidden = false;
    await cargarFranjasCita();
  } catch (e) { $('#c-msg').innerHTML = `<div class="msg err">${esc(e.message)}</div>`; }
}

async function cargarCitasPaciente() {
  const filas = await api('/api/citas?documento=' + encodeURIComponent(citaDoc));
  $('#t-citas').textContent = `· ${filas.length}`;
  citasPaciente = filas;
  table($('#c-tbl'), [
    { h: 'Fecha', f: r => esc(diaHora(r.fechaHora)) }, { h: 'Dosis', f: r => `${esc(r.vacuna)} · ${esc(r.numeroDosis)}` },
    { h: 'Establecimiento', k: 'establecimiento' },
    { h: 'Estado', f: r => `<span class="tag ${r.estado === 'PROGRAMADA' ? 'ÓPTIMA' : r.estado === 'ATENDIDA' ? 'BAJO' : 'ACEPTABLE'}">${esc(r.estado)}</span>` },
    { h: '', f: r => r.estado === 'PROGRAMADA' ? `<button class="b sec" data-acc="reprogramar" data-id="${r.idCita}">Reprogramar</button> <button class="b sec" data-acc="cancelar" data-id="${r.idCita}">Cancelar</button>` : '' }
  ], filas);
}

async function cargarFranjasCita() {
  const vacuna = $('#c-dosis').value.split('|')[0];
  const q = new URLSearchParams({ vacuna });
  if (yo.rol === 'CIUDADANO' && $('#c-est').value) q.set('idEstablecimiento', $('#c-est').value);
  const f = await api('/api/agenda/franjas?' + q);
  $('#c-franja').innerHTML = f.map(x => `<option value="${x.idHorario}">${esc(diaHora(x.fechaHora))} · ${esc(x.libres)} ${x.libres === 1 ? 'cupo' : 'cupos'}${yo.rol === 'CIUDADANO' ? ' · ' + esc(x.establecimiento) : ''}</option>`).join('');
  $('#c-sinfranjas').hidden = f.length > 0;
  $('#p-confirmar').hidden = f.length === 0;
}

function prepararCitas() {
  $('#c-est').innerHTML = '<option value="">Todos</option>' + cat.establecimientos.map(e => `<option value="${e.IdEstablecimiento}">${esc(e.Nombre)} (${esc(e.Distrito)})</option>`).join('');
  $('#c-lest').hidden = yo.rol !== 'CIUDADANO';
  $('#c-buscar').hidden = yo.rol === 'CIUDADANO';
  const buscar = () => elegirPacienteCita($('#c-doc').value.trim());
  $('#b-c-buscar').onclick = buscar;
  $('#c-doc').onkeydown = e => e.key === 'Enter' && buscar();
  $('#c-hijo').onchange = () => elegirPacienteCita($('#c-hijo').value);
  const recargar = () => cargarFranjasCita().catch(e => $('#c-msg').innerHTML = `<div class="msg err">${esc(e.message)}</div>`);
  $('#c-dosis').onchange = recargar; $('#c-est').onchange = recargar;

  // Cancelar y reprogramar (RN-16: hasta 24 horas antes; la base lo hace cumplir).
  const aviso = (ok, t) => $('#rep-msg').innerHTML = `<div class="msg ${ok ? 'ok' : 'err'}">${esc(t)}</div>`;
  const refrescarCitas = async () => { await elegirPacienteCita(citaDoc); };
  $('#c-tbl').onclick = async e => {
    const b = e.target.closest('button[data-acc]');
    if (!b) return;
    const cita = citasPaciente.find(x => x.idCita === +b.dataset.id);
    $('#rep-msg').innerHTML = '';
    if (b.dataset.acc === 'cancelar') {
      if (!confirm(`¿Cancelar la cita de ${cita.vacuna} del ${diaHora(cita.fechaHora)}?`)) return;
      try { await api(`/api/citas/${cita.idCita}/cancelar`, { method: 'POST' }); await refrescarCitas(); aviso(true, 'Cita cancelada. El cupo quedó libre.'); }
      catch (err) { aviso(false, err.message); }
      return;
    }
    citaEditando = cita;
    $('#rep-cita').textContent = `${cita.vacuna} · ${diaHora(cita.fechaHora)}`;
    try {
      const q = new URLSearchParams({ vacuna: cita.vacuna });
      const f = (await api('/api/agenda/franjas?' + q)).filter(x => x.fechaHora !== cita.fechaHora || x.establecimiento !== cita.establecimiento);
      $('#rep-franja').innerHTML = f.map(x => `<option value="${x.idHorario}">${esc(diaHora(x.fechaHora))} · ${esc(x.libres)} ${x.libres === 1 ? 'cupo' : 'cupos'} · ${esc(x.establecimiento)}</option>`).join('');
      $('#f-rep').hidden = false;
      if (!f.length) aviso(false, 'No hay otras franjas con cupo para esta vacuna.');
    } catch (err) { aviso(false, err.message); }
  };
  $('#rep-cancelar').onclick = () => { $('#f-rep').hidden = true; citaEditando = null; };
  $('#f-rep').onsubmit = async e => {
    e.preventDefault();
    if (!citaEditando || !$('#rep-franja').value) return;
    try {
      await api(`/api/citas/${citaEditando.idCita}/reprogramar`, json({ idHorario: +$('#rep-franja').value }));
      $('#f-rep').hidden = true; citaEditando = null;
      await refrescarCitas();
      aviso(true, 'Cita reprogramada.');
    } catch (err) { aviso(false, err.message); }   // p. ej. la franja se llenó: la cita original se conserva
  };
  $('#b-c-reservar').onclick = async () => {
    const [vacuna, dosis] = $('#c-dosis').value.split('|');
    const b = $('#b-c-reservar'); b.disabled = true;
    try {
      await api('/api/citas', json({ documento: citaDoc, vacuna, dosis: +dosis, idHorario: +$('#c-franja').value }));
      $('#c-msg').innerHTML = '<div class="msg ok">Cita reservada. Llegue 10 minutos antes con el DNI del paciente.</div>';
      await elegirPacienteCita(citaDoc);
      $('#c-msg').innerHTML = '<div class="msg ok">Cita reservada. Llegue 10 minutos antes con el DNI del paciente.</div>';
    } catch (e) {
      // Si la franja se llenó mientras elegía, se muestra el motivo y se refresca la lista de franjas.
      $('#c-msg').innerHTML = `<div class="msg err">${esc(e.message)}</div>`;
      recargar();
    } finally { b.disabled = false; }
  };
}

// ---- Horarios del establecimiento (solo JEFE_ESTABLECIMIENTO) ----
let horarios = [];
const hoyISO = (dias = 0) => { const d = new Date(); d.setDate(d.getDate() + dias); return d.toLocaleDateString('sv-SE'); };

async function cargarHorarios() {
  const filas = await api('/api/horarios?' + new URLSearchParams({ desde: $('#h-desde').value, hasta: $('#h-hasta').value }));
  horarios = filas;
  $('#t-hor').textContent = `· ${filas.length} franjas · ${filas.__ms} ms`;
  table($('#hor-tbl'), [
    { h: 'Fecha', f: r => fecha(r.fechaHora) }, { h: 'Hora', f: r => esc(String(r.fechaHora).slice(11, 16)) }, { h: 'Vacuna', k: 'vacuna' },
    { h: 'Cupo', n: 1, f: r => fmt(r.cupoMaximo) }, { h: 'Reservados', n: 1, f: r => fmt(r.ocupados) },
    { h: 'Libres', n: 1, f: r => fmt(Math.max(r.cupoMaximo - r.ocupados, 0)) },
    { h: 'Estado', f: r => r.activo ? '<span class="tag ÓPTIMA">Activa</span>' : '<span class="tag ACEPTABLE">Inactiva</span>' },
    { h: '', f: r => `<button class="b sec" data-id="${r.idHorario}">Editar</button>` }
  ], filas);
}

function prepararHorarios() {
  $('#h-desde').value = hoyISO(); $('#h-hasta').value = hoyISO(30); $('#h-fecha').min = hoyISO();
  $('#h-vac').innerHTML = [...new Set(cat.esquema.map(e => e.Codigo))].map(c => `<option>${esc(c)}</option>`).join('');
  $('#f-hor-rango').onsubmit = async e => {
    e.preventDefault();
    try { await cargarHorarios(); $('#he-msg').innerHTML = ''; } catch (err) { $('#he-msg').innerHTML = `<div class="msg err">${esc(err.message)}</div>`; }
  };
  $('#f-hor').onsubmit = async e => {
    e.preventDefault();
    try {
      await api('/api/horarios', json({ vacuna: $('#h-vac').value, fechaHora: `${$('#h-fecha').value}T${$('#h-hora').value}`, cupoMaximo: +$('#h-cupo').value }));
      $('#h-msg').innerHTML = '<div class="msg ok">Franja creada.</div>';
      await cargarHorarios();
    } catch (err) { $('#h-msg').innerHTML = `<div class="msg err">${esc(err.message)}</div>`; }
  };
  let editando = null;
  $('#hor-tbl').onclick = e => {
    const b = e.target.closest('button[data-id]');
    if (!b) return;
    editando = horarios.find(x => x.idHorario === +b.dataset.id);
    $('#he-franja').textContent = `${editando.vacuna} · ${fecha(editando.fechaHora)} ${String(editando.fechaHora).slice(11, 16)} (${fmt(editando.ocupados)} reservados)`;
    $('#he-cupo').value = editando.cupoMaximo; $('#he-activo').value = editando.activo ? '1' : '0';
    $('#f-hor-ed').hidden = false; $('#he-msg').innerHTML = ''; $('#he-cupo').focus();
  };
  $('#he-cancelar').onclick = () => { $('#f-hor-ed').hidden = true; editando = null; };
  $('#f-hor-ed').onsubmit = async e => {
    e.preventDefault();
    try {
      await api(`/api/horarios/${editando.idHorario}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cupoMaximo: +$('#he-cupo').value, activo: $('#he-activo').value === '1' }) });
      $('#f-hor-ed').hidden = true; editando = null;
      $('#he-msg').innerHTML = '<div class="msg ok">Franja actualizada.</div>';
      await cargarHorarios();
    } catch (err) { $('#he-msg').innerHTML = `<div class="msg err">${esc(err.message)}</div>`; }
  };
}

// ---- Auditoría de dosis (solo ADMINISTRADOR) ----
async function cargarAuditoria() {
  const r = await api('/api/auditoria?' + new URLSearchParams({ documento: $('#au-doc').value.trim(), operacion: $('#au-op').value, top: 200 }));
  $('#t-aud').textContent = `· ${r.length} registros · ${r.__ms} ms`;
  table($('#aud'), [
    { h: 'Fecha', f: r => esc(String(r.fecha).replace('T', ' ').slice(0, 19)) },
    { h: 'Operación', f: r => `<span class="tag ${r.operacion === 'D' ? 'CRÍTICA' : 'ACEPTABLE'}">${r.operacion === 'D' ? 'Eliminada' : 'Corregida'}</span>` },
    { h: 'Usuario', k: 'usuario' }, { h: 'DNI', k: 'numeroDocumento' }, { h: 'Paciente', k: 'paciente' },
    { h: 'Dosis', f: r => `${esc(r.codigoVacuna)} · ${esc(r.numeroDosis)}` },
    { h: 'Fecha anterior', f: r => fecha(r.fechaAplicacionAnterior) }, { h: 'Fecha nueva', f: r => fecha(r.fechaAplicacionNueva) },
    { h: 'Lote anterior → nuevo', f: r => `${esc(r.loteAnterior ?? '—')} → ${esc(r.loteNuevo ?? '—')}` }
  ], r);
}

// ---- Ciudadano: solo ve el carné de los hijos vinculados a su usuario (RN-17) ----
async function prepararHijos() {
  $('#p-filtro').hidden = true;   // el ciudadano no busca por DNI: elige entre sus hijos
  const hijos = await api('/api/mis-pacientes');
  const caja = $('#hijos');
  caja.hidden = false;
  if (!hijos.length) { caja.innerHTML = '<div class="msg ok">Aún no tiene hijos vinculados a su cuenta. Pida al establecimiento que los vincule.</div>'; return; }
  caja.innerHTML = hijos.map(h => `<button class="b sec" data-doc="${esc(h.numeroDocumento)}">${esc(h.nombre)} <small class="muted">(${esc(h.parentesco)})</small></button>`).join('');
  caja.onclick = e => {
    const b = e.target.closest('button[data-doc]');
    if (!b) return;
    $('#p-doc').value = b.dataset.doc;
    buscarPaciente();
  };
  caja.querySelector('button').click();
}

// ---- Alta de pacientes (solo VACUNADOR; el servidor lo vuelve a exigir) ----
function prepararAltaPaciente() {
  $('#npac').hidden = false;
  $('#n-dis').innerHTML = cat.distritos.map(d => `<option value="${esc(d.Ubigeo)}">${esc(d.Nombre)}</option>`).join('');
  $('#n-nac').max = new Date().toISOString().slice(0, 10);
  $('#f-npac').onsubmit = async e => {
    e.preventDefault();
    const doc = $('#n-doc').value.trim();
    try {
      await api('/api/pacientes', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
        tipoDocumento: $('#n-tipo').value, numeroDocumento: doc, nombres: $('#n-nom').value, apellidoPaterno: $('#n-pat').value,
        apellidoMaterno: $('#n-mat').value || null, fechaNacimiento: $('#n-nac').value, sexo: $('#n-sex').value,
        ubigeo: $('#n-dis').value, direccion: $('#n-dir').value || null, telefono: $('#n-tel').value || null }) });
      $('#f-npac').reset();
      $('#n-msg').innerHTML = '<div class="msg ok">Paciente registrado. Se muestra su carné.</div>';
      $('#p-doc').value = doc;
      await buscarPaciente();
    } catch (err) { $('#n-msg').innerHTML = `<div class="msg err">${esc(err.message)}</div>`; }
  };
}

// ---- Administración de usuarios (solo ADMINISTRADOR; el servidor lo vuelve a exigir) ----
let usuarios = [];

async function cargarUsuarios() {
  usuarios = await api('/api/usuarios');
  table($('#u-tbl'), [
    { h: 'Usuario', k: 'nombreUsuario' }, { h: 'Nombre', k: 'nombreCompleto' },
    { h: 'Rol', f: r => esc(ROLES[r.rol] ?? r.rol) }, { h: 'Establecimiento', f: r => esc(r.establecimiento ?? '—') },
    { h: 'Estado', f: r => `<span class="tag ${r.activo ? 'ÓPTIMA' : 'CRÍTICA'}">${r.activo ? 'Activo' : 'Inactivo'}</span>` },
    { h: '', f: r => r.nombreUsuario === yo.usuario ? '' :
        `<button class="b sec" data-id="${r.idUsuario}" data-activo="${r.activo ? 0 : 1}">${r.activo ? 'Desactivar' : 'Activar'}</button>` }
  ], usuarios.map(u => ({ ...u })));
}

function prepararUsuarios() {
  $('#u-rol').innerHTML = Object.entries(ROLES).map(([k, v]) => `<option value="${k}">${esc(v)}</option>`).join('');
  $('#u-est').innerHTML = cat.establecimientos.map(e => `<option value="${e.IdEstablecimiento}">${esc(e.Nombre)}</option>`).join('');
  const ajustar = () => {
    const rol = $('#u-rol').value;
    $('#u-lest').hidden = !['JEFE_ESTABLECIMIENTO', 'VACUNADOR'].includes(rol);
    $('#u-lvac').hidden = rol !== 'VACUNADOR';
    $('#u-vac').innerHTML = cat.personal.filter(p => p.IdEstablecimiento == $('#u-est').value)
      .map(p => `<option value="${p.IdVacunador}">${esc(p.Nombre)}</option>`).join('');
  };
  $('#u-rol').onchange = ajustar; $('#u-est').onchange = ajustar; ajustar();

  $('#f-usu').onsubmit = async e => {
    e.preventDefault();
    const rol = $('#u-rol').value;
    const conEst = !$('#u-lest').hidden, conVac = !$('#u-lvac').hidden;
    try {
      await api('/api/usuarios', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
        usuario: $('#u-nom').value.trim(), nombre: $('#u-ape').value.trim(), clave: $('#u-cla').value, rol,
        idEstablecimiento: conEst ? +$('#u-est').value : null, idVacunador: conVac ? +$('#u-vac').value : null }) });
      $('#u-msg').innerHTML = '<div class="msg ok">Usuario creado.</div>';
      $('#f-usu').reset(); ajustar();
      await cargarUsuarios();
    } catch (err) { $('#u-msg').innerHTML = `<div class="msg err">${esc(err.message)}</div>`; }
  };

  $('#u-tbl').onclick = async e => {
    const b = e.target.closest('button[data-id]');
    if (!b) return;
    const u = usuarios.find(x => x.idUsuario === +b.dataset.id);
    try {
      await api('/api/usuarios/' + u.idUsuario, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
        nombre: u.nombreCompleto, rol: u.rol, idEstablecimiento: u.idEstablecimiento, idVacunador: u.idVacunador, activo: b.dataset.activo === '1' }) });
      $('#u-msg').innerHTML = '';
      await cargarUsuarios();
    } catch (err) { $('#u-msg').innerHTML = `<div class="msg err">${esc(err.message)}</div>`; }
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
  if (tabs.includes('citas')) prepararCitas();
  if (tabs.includes('stock')) prepararStock();
  if (tabs.includes('horarios')) prepararHorarios();
  if (tabs.includes('usuarios')) prepararUsuarios();
  if (tabs.includes('auditoria')) $('#b-aud').onclick = cargarAuditoria;
  if (yo.rol === 'VACUNADOR') prepararAltaPaciente();
  if (yo.rol === 'CIUDADANO') await prepararHijos();

  document.querySelector(`nav button[data-tab="${tabs[0]}"]`).click();
  if (tabs[0] === 'resumen') await cargarResumen();
}
init().catch(e => { if (e.message !== 'Sesión no iniciada.') $('#aviso').innerHTML = `<div class="msg err">No se pudo cargar la aplicación: ${esc(e.message)}</div>`; });
