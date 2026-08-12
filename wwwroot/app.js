const elements = {
  tracked: document.getElementById('trackedCount'),
  buy: document.getElementById('buyCount'),
  sell: document.getElementById('sellCount'),
  hold: document.getElementById('holdCount'),
  grid: document.getElementById('advisoryGrid'),
  history: document.getElementById('historyTable'),
  status: document.getElementById('connectionStatus'),
  form: document.getElementById('addSymbolForm'),
  input: document.getElementById('addSymbolInput'),
  button: document.getElementById('addSymbolBtn'),
  message: document.getElementById('addSymbolMsg'),
  themeToggle: document.getElementById('themeToggle'),
  themeSelect: document.getElementById('themeSelect'),
  refreshSelect: document.getElementById('refreshSelect')
};

let historyRefreshTimer;
let latestHistory = [];

const formatPrice = value => new Intl.NumberFormat(undefined, {
  minimumFractionDigits: Number(value) < 1 ? 4 : 2,
  maximumFractionDigits: Number(value) < 1 ? 8 : 2
}).format(Number(value));

function setConnection(label, state) {
  elements.status.className = `connection-pill ${state}`;
  elements.status.lastElementChild.textContent = label;
}

function renderAdvisories(advisories) {
  if (!Array.isArray(advisories) || advisories.length === 0) {
    elements.grid.innerHTML = '<div class="empty-state">No markets are currently being tracked.</div>';
    ['tracked', 'buy', 'sell', 'hold'].forEach(key => { elements[key].textContent = '0'; });
    return;
  }

  const counts = { Buy: 0, Sell: 0, Hold: 0 };
  advisories.forEach(item => { counts[item.signal] = (counts[item.signal] || 0) + 1; });
  elements.tracked.textContent = advisories.length;
  elements.buy.textContent = counts.Buy || 0;
  elements.sell.textContent = counts.Sell || 0;
  elements.hold.textContent = counts.Hold || 0;

  elements.grid.replaceChildren(...advisories.map(createAdvisoryCard));
  advisories.forEach(item => {
    if (Array.isArray(item.prices) && item.prices.length > 1) {
      drawSparkline(document.getElementById(`spark-${safeId(item.symbol)}`), item.prices, item.signal);
    }
  });
}

function createAdvisoryCard(item) {
  const card = document.createElement('article');
  card.className = 'advisory-card';
  const isLive = String(item.status).toLowerCase() === 'live';
  const signalClass = String(item.signal).toLowerCase();
  const statusClass = String(item.status).toLowerCase();

  card.innerHTML = `
    <div class="card-top">
      <div><h3 class="symbol-name"></h3><p class="provider"></p></div>
      <button class="remove-btn" type="button">Remove</button>
    </div>
    <p class="price ${isLive ? '' : 'waiting'}"></p>
    <span class="price-label">Last price</span>
    <div class="chart-wrap"><canvas id="spark-${safeId(item.symbol)}"></canvas></div>
    <div class="signal-row"><span class="signal-badge ${signalClass}"></span><span class="confidence"><strong></strong> confidence</span></div>
    <p class="reason"></p>
    <div class="risk-grid">
      <div><span>Entry</span><strong class="entry-price"></strong></div>
      <div><span>SL</span><strong class="stop-loss"></strong></div>
      <div><span>TP1</span><strong class="take-profit1"></strong></div>
      <div><span>TP2</span><strong class="take-profit2"></strong></div>
    </div>
    <div class="data-line"><span class="data-status ${statusClass}"></span><time></time></div>`;

  const confidenceValue = item.confidence != null ? Number(item.confidence) : Number(item.technicalConfidence || 0);
  const formattedConfidence = Number.isFinite(confidenceValue) ? `${Math.round(confidenceValue * 100)}%` : '0%';

  card.querySelector('.symbol-name').textContent = item.symbol;
  card.querySelector('.provider').textContent = item.provider || 'Market provider';
  const remove = card.querySelector('.remove-btn');
  remove.dataset.symbol = item.symbol;
  remove.setAttribute('aria-label', `Remove ${item.symbol}`);
  card.querySelector('.price').textContent = isLive ? formatPrice(item.price) : 'Awaiting data';
  card.querySelector('.signal-badge').textContent = item.signal;
  card.querySelector('.confidence strong').textContent = formattedConfidence;
  card.querySelector('.reason').textContent = item.reason;
  card.querySelector('.entry-price').textContent = item.entryPrice != null ? formatPrice(item.entryPrice) : '—';
  card.querySelector('.stop-loss').textContent = item.stopLoss != null ? formatPrice(item.stopLoss) : '—';
  card.querySelector('.take-profit1').textContent = item.takeProfit1 != null ? formatPrice(item.takeProfit1) : '—';
  card.querySelector('.take-profit2').textContent = item.takeProfit2 != null ? formatPrice(item.takeProfit2) : '—';
  card.querySelector('.data-status').textContent = item.statusMessage || item.status;
  card.querySelector('time').textContent = new Date(item.time).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  return card;
}

function renderHistory(history) {
  latestHistory = Array.isArray(history) ? history : [];
  renderPerformance(latestHistory);
  elements.history.replaceChildren();
  if (!Array.isArray(history) || history.length === 0) {
    const row = elements.history.insertRow();
    const cell = row.insertCell();
    cell.colSpan = 6;
    cell.className = 'table-empty';
    cell.textContent = 'No signal changes recorded yet.';
    return;
  }

  history.slice().reverse().forEach(item => {
    const row = elements.history.insertRow();
    const values = [
      new Date(item.time).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }),
      item.symbol,
      formatPrice(item.price),
      item.signal,
      `${Math.round(Number(item.confidence) * 100)}%`,
      item.reason
    ];
    values.forEach((value, index) => {
      const cell = row.insertCell();
      cell.textContent = value;
      if (index === 3) cell.className = `table-signal ${String(item.signal).toLowerCase()}`;
    });
  });
}

function renderPerformance(history) {
  const directional = history.filter(item => item.signal === 'Buy' || item.signal === 'Sell');
  const average = history.length
    ? history.reduce((sum, item) => sum + Number(item.confidence || 0), 0) / history.length
    : 0;
  document.getElementById('performanceTotal').textContent = history.length;
  document.getElementById('performanceDirectional').textContent = directional.length;
  document.getElementById('performanceConfidence').textContent = `${Math.round(average * 100)}%`;

  const grouped = history.reduce((result, item) => {
    const market = result[item.symbol] ||= { total: 0, Buy: 0, Sell: 0, Hold: 0 };
    market.total += 1;
    market[item.signal] = (market[item.signal] || 0) + 1;
    return result;
  }, {});
  const container = document.getElementById('marketPerformance');
  container.replaceChildren();
  if (!Object.keys(grouped).length) {
    const empty = document.createElement('p');
    empty.className = 'muted-copy';
    empty.textContent = 'Signal activity will appear after the first advisory changes.';
    container.appendChild(empty);
    return;
  }
  Object.entries(grouped).sort(([a], [b]) => a.localeCompare(b)).forEach(([symbol, counts]) => {
    const row = document.createElement('div');
    row.className = 'market-row';
    [symbol, `${counts.total} changes`, `${counts.Buy} buy`, `${counts.Sell} sell`].forEach(value => {
      const span = document.createElement('span'); span.textContent = value; row.appendChild(span);
    });
    container.appendChild(row);
  });
}

async function fetchHistory() {
  try {
    const response = await fetch('/api/history');
    if (!response.ok) throw new Error('History request failed');
    renderHistory(await response.json());
  } catch {
    elements.history.innerHTML = '<tr><td colspan="6" class="table-empty">Signal history is temporarily unavailable.</td></tr>';
  }
}

function connectWebSocket() {
  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  const socket = new WebSocket(`${scheme}://${location.host}/ws/advisories`);
  setConnection('Connecting', 'connecting');
  socket.addEventListener('open', () => setConnection('Live connection', 'live'));
  socket.addEventListener('message', event => {
    try { renderAdvisories(JSON.parse(event.data)); } catch { setConnection('Data error', 'error'); }
  });
  socket.addEventListener('close', () => {
    setConnection('Reconnecting', 'error');
    window.setTimeout(connectWebSocket, 2500);
  });
  socket.addEventListener('error', () => socket.close());
}

elements.form.addEventListener('submit', async event => {
  event.preventDefault();
  const symbol = elements.input.value.trim().toUpperCase();
  if (!symbol) return;
  elements.button.disabled = true;
  elements.button.textContent = 'Checking…';
  elements.message.textContent = '';
  elements.message.className = 'form-message';

  try {
    const response = await fetch('/api/symbols/add', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ symbol })
    });
    const body = await response.json().catch(() => ({}));
    elements.message.textContent = body.message || body.error || 'Unable to add that market.';
    elements.message.classList.add(response.ok ? 'success' : 'error');
    if (response.ok) elements.input.value = '';
  } catch {
    elements.message.textContent = 'The server could not be reached.';
    elements.message.classList.add('error');
  } finally {
    elements.button.disabled = false;
    elements.button.textContent = 'Add market';
  }
});

elements.grid.addEventListener('click', async event => {
  const button = event.target.closest('.remove-btn');
  if (!button) return;
  button.disabled = true;
  button.textContent = 'Removing…';
  try {
    const response = await fetch('/api/symbols/remove', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ symbol: button.dataset.symbol })
    });
    if (!response.ok) throw new Error('Remove failed');
  } catch {
    button.disabled = false;
    button.textContent = 'Remove';
  }
});

function drawSparkline(canvas, prices, signal) {
  if (!canvas) return;
  const ratio = window.devicePixelRatio || 1;
  const rect = canvas.getBoundingClientRect();
  canvas.width = Math.max(1, rect.width * ratio);
  canvas.height = Math.max(1, rect.height * ratio);
  const context = canvas.getContext('2d');
  context.scale(ratio, ratio);
  const values = prices.map(Number);
  const min = Math.min(...values), max = Math.max(...values), range = max - min || 1;
  const color = signal === 'Buy' ? '#35d49a' : signal === 'Sell' ? '#ff6b7a' : '#f5b95f';
  const points = values.map((price, index) => ({ x: index / (values.length - 1) * rect.width, y: rect.height - 5 - ((price - min) / range) * (rect.height - 10) }));
  const gradient = context.createLinearGradient(0, 0, 0, rect.height);
  gradient.addColorStop(0, `${color}33`); gradient.addColorStop(1, `${color}00`);
  context.beginPath(); context.moveTo(points[0].x, rect.height); points.forEach(point => context.lineTo(point.x, point.y)); context.lineTo(points.at(-1).x, rect.height); context.closePath(); context.fillStyle = gradient; context.fill();
  context.beginPath(); points.forEach((point, index) => index ? context.lineTo(point.x, point.y) : context.moveTo(point.x, point.y)); context.strokeStyle = color; context.lineWidth = 1.5; context.stroke();
}

function safeId(value) { return String(value).replace(/[^a-zA-Z0-9_-]/g, ''); }

function showView(viewId) {
  document.querySelectorAll('.app-view').forEach(view => view.classList.toggle('active-view', view.id === viewId));
  document.querySelectorAll('.nav-item[data-view]').forEach(link => link.classList.toggle('active', link.dataset.view === viewId));
  const active = document.querySelector(`.nav-item[data-view="${viewId}"]`);
  const title = active?.textContent.trim() || 'Live signals';
  document.querySelector('.topbar h1').textContent = title;
}

document.querySelectorAll('.nav-item[data-view]').forEach(link => link.addEventListener('click', event => {
  event.preventDefault();
  showView(link.dataset.view);
  history.replaceState(null, '', link.getAttribute('href'));
}));

function resolvedTheme(preference) {
  return preference === 'system'
    ? (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark')
    : preference;
}

function applyTheme(preference) {
  const theme = resolvedTheme(preference);
  document.documentElement.dataset.theme = theme;
  elements.themeSelect.value = preference;
  elements.themeToggle.querySelector('[aria-hidden]').textContent = theme === 'dark' ? 'L' : 'D';
  elements.themeToggle.querySelector('.theme-label').textContent = theme === 'dark' ? 'Light' : 'Dark';
  elements.themeToggle.setAttribute('aria-label', `Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`);
}

function saveTheme(preference) {
  localStorage.setItem('trading-theme', preference);
  applyTheme(preference);
  showSaved();
}

elements.themeToggle.addEventListener('click', () => saveTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark'));
elements.themeSelect.addEventListener('change', () => saveTheme(elements.themeSelect.value));
matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => {
  if ((localStorage.getItem('trading-theme') || 'dark') === 'system') applyTheme('system');
});

function scheduleHistoryRefresh(interval) {
  clearInterval(historyRefreshTimer);
  localStorage.setItem('history-refresh', interval);
  historyRefreshTimer = window.setInterval(fetchHistory, Number(interval));
}

elements.refreshSelect.addEventListener('change', () => { scheduleHistoryRefresh(elements.refreshSelect.value); showSaved(); });

function showSaved() {
  const label = document.getElementById('settingsSaved');
  label.textContent = 'Saved locally';
  window.setTimeout(() => { label.textContent = ''; }, 1800);
}

connectWebSocket();
fetchHistory();
const savedTheme = localStorage.getItem('trading-theme') || 'dark';
applyTheme(savedTheme);
const savedRefresh = localStorage.getItem('history-refresh') || '5000';
elements.refreshSelect.value = savedRefresh;
scheduleHistoryRefresh(savedRefresh);
const route = document.querySelector(`.nav-item[href="${location.hash}"]`);
if (route) showView(route.dataset.view);
