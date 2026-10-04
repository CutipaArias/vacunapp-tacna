// ---- Tablero de cobertura (RF-13, CU12, RN-21) ----
// Se carga antes que panel.js (cuyo arranque la llama); usa sus ayudantes ($, api, esc, fmt, pct, table, tag, bar, qs) y Chart.js servido desde /vendor.
// El semáforo y el riesgo los decide la base; aquí solo se dibujan.
const COLOR_SEMAFORO = { 'ÓPTIMA': '--ok', 'ACEPTABLE': '--warn', 'CRÍTICA': '--bad' };
const cssVar = nombre => getComputedStyle(document.documentElement).getPropertyValue(nombre).trim();
const graficos = {};

function prepararDashboard() {
  const vacunas = [...new Set(cat.esquema.map(e => e.Codigo))];
  $('#d-vac').innerHTML = vacunas.map(c => `<option${c === 'SPR' ? ' selected' : ''}>${esc(c)}</option>`).join('');
  $('#b-dash').onclick = cargarDashboard;
}

function dibujar(id, config) {
  graficos[id]?.destroy();
  graficos[id] = new Chart($('#' + id), config);
}

async function cargarDashboard() {
  $('#dash-msg').innerHTML = '';
  let d;
  try { d = await api('/api/dashboard?' + qs({ vacuna: $('#d-vac').value, dosis: $('#d-dos').value })); }
  catch (e) { $('#dash-msg').innerHTML = `<div class="msg err">${esc(e.message)}</div>`; return; }

  const f = d.filtro, r = d.resumen, u = d.umbrales;
  $('#t-dash').textContent = `· ${f.vacuna} · ${f.descripcion} · ${d.__ms} ms`;
  $('#dash-kpis').innerHTML = [
    ['Cobertura regional', pct(r.coberturaRegional), ''],
    [`ÓPTIMA (≥ ${u.optima} %)`, `${fmt(r.optima)} <small class="muted">de ${fmt(r.distritos)}</small>`, 'sem-ok'],
    [`ACEPTABLE (≥ ${u.aceptable} %)`, fmt(r.aceptable), 'sem-warn'],
    [`CRÍTICA (< ${u.aceptable} %)`, fmt(r.critica), 'sem-bad'],
    ['Riesgo de sarampión ALTO', `${fmt(r.distritosEnRiesgoAlto)} <small class="muted">distritos</small>`, 'sem-bad'],
    ['Riesgo de sarampión MEDIO', `${fmt(r.distritosEnRiesgoMedio)} <small class="muted">distritos</small>`, 'sem-warn'],
  ].map(([t, v, c]) => `<div class="kpi ${c}"><span>${t}</span><b>${v}</b></div>`).join('');

  if (typeof Chart === 'undefined') {
    $('#dash-msg').innerHTML = '<div class="msg err">No se pudo cargar la biblioteca de gráficos; la tabla muestra los mismos datos.</div>';
  } else {
    Chart.defaults.color = cssVar('--muted');
    Chart.defaults.borderColor = cssVar('--line');
    Chart.defaults.font.family = '"Segoe UI", system-ui, sans-serif';
    const etiquetas = d.distritos.map(x => x.distrito);
    const lineaMeta = (nombre, valor, color) => ({
      type: 'line', label: nombre, data: etiquetas.map(() => valor), borderColor: cssVar(color), borderDash: [6, 4],
      borderWidth: 2, pointRadius: 0, pointHitRadius: 0, order: 0,
    });
    dibujar('g-dist', {
      type: 'bar',
      data: {
        labels: etiquetas,
        datasets: [
          { label: `Cobertura ${f.vacuna} ${f.descripcion} (%)`, data: d.distritos.map(x => x.cobertura), order: 1,
            backgroundColor: d.distritos.map(x => cssVar(COLOR_SEMAFORO[x.clasificacion])) },
          lineaMeta(`Meta ${u.optima} %`, u.optima, '--ok'),
          lineaMeta(`Mínimo aceptable ${u.aceptable} %`, u.aceptable, '--warn'),
        ],
      },
      options: {
        responsive: true, maintainAspectRatio: false,
        scales: { y: { min: 0, max: 100, ticks: { callback: v => v + ' %' } }, x: { ticks: { maxRotation: 90, minRotation: 45 } } },
        plugins: {
          legend: { position: 'bottom' },
          tooltip: { callbacks: { afterLabel: c => c.datasetIndex === 0 ? `${fmt(d.distritos[c.dataIndex].vacunados)} de ${fmt(d.distritos[c.dataIndex].elegibles)} · ${d.distritos[c.dataIndex].clasificacion}` : '' } },
        },
      },
    });
    dibujar('g-sem', {
      type: 'doughnut',
      data: {
        labels: ['ÓPTIMA', 'ACEPTABLE', 'CRÍTICA'],
        datasets: [{ data: [r.optima, r.aceptable, r.critica], backgroundColor: ['--ok', '--warn', '--bad'].map(cssVar), borderColor: cssVar('--panel') }],
      },
      options: { responsive: true, maintainAspectRatio: false, plugins: { legend: { position: 'bottom' } } },
    });
  }

  table($('#dash-tbl'), [
    { h: 'Distrito', k: 'distrito' }, { h: 'Provincia', k: 'provincia' },
    { h: 'Elegibles', n: 1, f: x => fmt(x.elegibles) }, { h: 'Vacunados', n: 1, f: x => fmt(x.vacunados) },
    { h: 'Cobertura', f: x => bar(x.cobertura) }, { h: 'Semáforo', f: x => tag(x.clasificacion) },
    { h: 'Riesgo de sarampión', f: x => tag(x.riesgoSarampion) + (x.broteActivo ? ` <span class="muted">brote · ${x.casosConfirmados} casos</span>` : '') },
  ], d.distritos);
}
