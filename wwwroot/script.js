'use strict';
// Ліміти дублюють серверні (TasksController) - перевірка на клієнті економить запити
const LIM = { minCities: 3, cities: 20, gens: 1000, minPop: 10, pop: 200, active: 5 };
const POLL_MS = 2000;
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const ST = { Pending: ['У черзі', '--wa'], Processing: ['Виконується', '--ac'], Completed: ['Готово', '--ok'], Failed: ['Помилка', '--er'], Canceled: ['Скасовано', '--mu'] };
const isActive = t => t.status === 'Pending' || t.status === 'Processing';
const ts = s => s ? new Date(/(Z|[+-]\d\d:?\d\d)$/i.test(s) ? s : s + 'Z') : null; // сервер віддає UTC
const fmtDate = s => ts(s)?.toLocaleString('uk-UA') ?? '—';
const fmtDur = sec => { sec = Math.max(0, Math.round(sec)); return sec < 60 ? sec + ' с' : Math.floor(sec / 60) + ' хв ' + sec % 60 + ' с'; };
const badge = st => { const [l, c] = ST[st] || [st, '--mu']; return `<span class="badge" style="--c:var(${c})">${l}</span>`; };

let token = localStorage.getItem('token'), username = localStorage.getItem('user') || '';
let cities = [], tasks = [], details = {}, prevStatus = {}, timer, mode = 'login';

function toast(msg, type = '') {
    const d = document.createElement('div');
    d.className = 'toast ' + type; d.textContent = msg;
    $('#toasts').append(d); setTimeout(() => d.remove(), 5000);
}

async function api(path, opts = {}) {
    const r = await fetch('/api' + path, {
        method: opts.method || 'GET',
        headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) },
        body: opts.body ? JSON.stringify(opts.body) : undefined
    });
    const sb = r.headers.get('X-Served-By'); if (sb) $('#srv').textContent = 'Відповів сервер: ' + sb;
    let data = null; try { data = await r.json(); } catch { }
    if (r.status === 401 && token) { logout('Сесія закінчилась. Увійдіть знову.'); throw new Error('401'); }
    if (!r.ok) {
        const e = new Error(data?.message || data?.title || `Помилка сервера (${r.status})`);
        e.status = r.status; throw e;
    }
    return data;
}

// ---------- Авторизація ----------
function claims() {
    try { return JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/'))); } catch { return {}; }
}
function logout(msg) {
    token = null; localStorage.removeItem('token'); clearTimeout(timer);
    $('#app').classList.add('hidden'); $('#auth').classList.remove('hidden');
    if (msg) toast(msg, 'er');
}
function enter() {
    $('#auth').classList.add('hidden'); $('#app').classList.remove('hidden');
    $('#who').textContent = username; tasks = []; details = {}; prevStatus = {};
    if (!cities.length) genCities();
    tick();
}
async function doAuth() {
    const u = $('#u').value.trim(), p = $('#p').value, err = $('#auth-err');
    err.textContent = '';
    if (!u || p.length < 4) { err.textContent = 'Введіть логін і пароль (мінімум 4 символи).'; return; }
    $('#auth-btn').disabled = true;
    try {
        if (mode === 'register') { await api('/auth/register', { method: 'POST', body: { username: u, password: p } }); toast('Реєстрація успішна', 'ok'); }
        const d = await api('/auth/login', { method: 'POST', body: { username: u, password: p } });
        token = d.token; username = u; localStorage.setItem('token', token); localStorage.setItem('user', u);
        $('#p').value = ''; enter();
    } catch (e) { err.textContent = e.message; }
    finally { $('#auth-btn').disabled = false; }
}

// ---------- Малювання маршруту ----------
function draw(cv, pts, route) {
    const dpr = devicePixelRatio || 1, w = cv.clientWidth, h = cv.clientHeight;
    cv.width = w * dpr; cv.height = h * dpr;
    const g = cv.getContext('2d'); g.scale(dpr, dpr);
    if (!pts.length) return;
    const css = getComputedStyle(document.documentElement), ac = css.getPropertyValue('--ac'), rt = css.getPropertyValue('--rt'), ink = css.getPropertyValue('--ink');
    const xs = pts.map(p => p.x), ys = pts.map(p => p.y), pad = 24, x0 = Math.min(...xs), y0 = Math.min(...ys);
    const s = Math.min((w - 2 * pad) / ((Math.max(...xs) - x0) || 1), (h - 2 * pad) / ((Math.max(...ys) - y0) || 1));
    const P = i => [pad + (pts[i].x - x0) * s, pad + (pts[i].y - y0) * s];
    if (route && route.length > 1) {
        g.strokeStyle = rt; g.lineWidth = 2; g.beginPath();
        route.forEach((c, k) => { const [x, y] = P(c); k ? g.lineTo(x, y) : g.moveTo(x, y); });
        g.closePath(); g.stroke();
    }
    g.font = '11px sans-serif'; g.textAlign = 'center';
    pts.forEach((_, i) => {
        const [x, y] = P(i);
        g.fillStyle = ac; g.beginPath(); g.arc(x, y, 5, 0, 7); g.fill();
        if (route) { g.fillStyle = ink; g.fillText(i, x, y - 9); }
    });
}
const routeLen = (c, r) => r.reduce((s, a, k) => { const b = c[r[(k + 1) % r.length]]; return s + Math.hypot(c[a].x - b.x, c[a].y - b.y); }, 0);

// ---------- Форма нової задачі ----------
function genCities() {
    const n = Math.round(+$('#n').value);
    if (!(n >= LIM.minCities && n <= LIM.cities)) { $('#form-err').textContent = `Кількість міст: від ${LIM.minCities} до ${LIM.cities}.`; return; }
    cities = Array.from({ length: n }, () => ({ x: Math.round(Math.random() * 100), y: Math.round(Math.random() * 100) }));
    $('#coords').value = cities.map(c => `${c.x} ${c.y}`).join('\n');
    $('#form-err').textContent = ''; draw($('#prev'), cities);
}
function parseCoords() {
    const rows = $('#coords').value.split('\n').map(l => l.trim()).filter(Boolean).map(l => l.split(/[\s,;]+/).map(Number));
    cities = rows.every(r => r.length === 2 && r.every(Number.isFinite)) ? rows.map(([x, y]) => ({ x, y })) : [];
    if (cities.length) $('#n').value = cities.length;
    draw($('#prev'), cities);
}
function validate() {
    const e = [], g = +$('#gen').value, p = +$('#pop').value;
    if (!cities.length) e.push('Координати некоректні: кожен рядок має містити два числа.');
    else if (cities.length < LIM.minCities || cities.length > LIM.cities) e.push(`Кількість міст: від ${LIM.minCities} до ${LIM.cities}.`);
    else if (+$('#n').value !== cities.length) e.push(`У полі вказано ${$('#n').value} міст, а координат ${cities.length}. Допустимо від ${LIM.minCities} до ${LIM.cities}: натисніть «Згенерувати міста» або виправте список.`);
    if (!Number.isInteger(g) || g < 1 || g > LIM.gens) e.push(`Ітерацій: від 1 до ${LIM.gens}.`);
    if (!Number.isInteger(p) || p < LIM.minPop || p > LIM.pop) e.push(`Розмір популяції: від ${LIM.minPop} до ${LIM.pop}.`);
    if (tasks.filter(isActive).length >= LIM.active) e.push(`Вже запущено ${LIM.active} задач. Дочекайтесь завершення або скасуйте одну.`);
    return e;
}
async function submit() {
    const e = validate(); $('#form-err').innerHTML = e.map(esc).join('<br>');
    if (e.length) return;
    const m = cities.map(a => cities.map(b => +Math.hypot(a.x - b.x, a.y - b.y).toFixed(4)));
    $('#run').disabled = true;
    try {
        const r = await api('/tasks', { method: 'POST', body: { distanceMatrix: m, generations: +$('#gen').value, populationSize: +$('#pop').value } });
        localStorage.setItem('c_' + r.taskId, JSON.stringify(cities)); // координати потрібні лише для малювання
        toast('Задачу створено', 'ok'); await refresh();
    } catch (x) { $('#form-err').textContent = x.message; }
    finally { $('#run').disabled = false; }
}

// ---------- Поточні задачі та історія ----------
const stage = t => t.status === 'Pending' ? 'Очікує на виконання' : t.progress < 5 ? 'Ініціалізація популяції' : t.progress < 95 ? 'Еволюція поколінь' : 'Побудова маршруту';

function renderActive() {
    const a = tasks.filter(isActive);
    $('#act-cnt').textContent = `${a.length} з ${LIM.active}`;
    $('#active').innerHTML = a.length ? a.map(t => {
        const d = details[t.id] || {}, since = ts(d.startedAt || t.createdAt);
        const queue = t.status !== 'Pending' ? '' : `<div class="queue">Задачу поставлено в чергу.
      ${d.queuePosition ? ` Позиція: <b>${d.queuePosition}</b>.` : ''}
      ${d.estimatedWaitSeconds != null ? ` Орієнтовний час очікування: <b>${fmtDur(d.estimatedWaitSeconds)}</b>.` : ''}</div>`;
        return `<div class="task"><div><b>#${t.id.slice(0, 8)}</b>${badge(t.status)}</div>
      <div class="bar"><i style="width:${t.progress}%"></i></div>
      <div class="mu"><span>${stage(t)}: ${t.progress}%</span>
      <span>${d.assignedServerId ? 'Сервер ' + esc(d.assignedServerId) + ', ' : ''}${fmtDur((Date.now() - since) / 1000)}</span>
      <button class="ghost danger" data-cancel="${t.id}">Скасувати</button></div>${queue}</div>`;
    }).join('') : '<p class="mu">Немає активних задач. Створіть нову в формі зліва.</p>';
}
function renderHist() {
    const h = tasks.filter(t => !isActive(t));
    $('#hist').innerHTML = h.length ? `<table><tr><th>Створено</th><th>Статус</th><th>Прогрес</th><th>Загальний час</th><th>Сервер</th><th></th></tr>${h.map(t =>
        `<tr><td>${fmtDate(t.createdAt)}</td><td>${badge(t.status)}</td><td>${t.progress}%</td>
     <td>${t.completedAt ? fmtDur((ts(t.completedAt) - ts(t.createdAt)) / 1000) : '—'}</td>
     <td>${esc(t.assignedServerId || '—')}</td>
     <td><button class="ghost" data-open="${t.id}">Відкрити</button></td></tr>`).join('')}</table>`
        : '<p class="mu">Історія порожня.</p>';
}
async function refresh() {
    tasks = await api('/tasks');
    await Promise.all(tasks.filter(isActive).map(async t => { try { details[t.id] = await api('/tasks/' + t.id); } catch { } }));
    tasks.forEach(t => {
        if (prevStatus[t.id] && isActive({ status: prevStatus[t.id] }) && !isActive(t))
            toast(`Задача #${t.id.slice(0, 8)}: ${ST[t.status][0].toLowerCase()}`, t.status === 'Completed' ? 'ok' : t.status === 'Failed' ? 'er' : '');
        prevStatus[t.id] = t.status;
    });
    renderActive(); renderHist();
}
async function openTask(id) {
    const dlg = $('#dlg'), body = $('#dlg-body'); body.innerHTML = '<p class="mu">Завантаження...</p>'; dlg.showModal();
    try {
        const d = await api('/tasks/' + id), c = JSON.parse(localStorage.getItem('c_' + id) || 'null'), r = d.resultRoute;
        body.innerHTML = `<h2>Задача #${id.slice(0, 8)} ${badge(d.status)}</h2>
      <p class="mu">Створено: ${fmtDate(d.createdAt)}<br>Початок: ${fmtDate(d.startedAt)}<br>Завершено: ${fmtDate(d.completedAt)}
      ${d.assignedServerId ? '<br>Сервер: ' + esc(d.assignedServerId) : ''}</p>
      ${d.error ? `<p class="err">${esc(d.error)}</p>` : ''}
      ${r ? `<p><b>Маршрут:</b> ${r.join(' → ')}</p>${c ? `<p><b>Довжина:</b> ${routeLen(c, r).toFixed(2)}</p><canvas id="rc"></canvas>` : '<p class="mu">Координати міст збережені лише в браузері, де створювали задачу.</p>'}` : ''}`;
        if (r && c) draw($('#rc'), c, r);
    } catch (e) { body.innerHTML = `<p class="err">${esc(e.message)}</p>`; }
}

// ---------- Опитування сервера ----------
async function tick() {
    clearTimeout(timer);
    if (!token) return;
    if (claims().exp * 1000 < Date.now()) return logout('Сесія закінчилась. Увійдіть знову.');
    try { await refresh(); } catch { }
    timer = setTimeout(tick, POLL_MS);
}

// ---------- Події ----------
$$('.tabs button').forEach(b => b.onclick = () => {
    mode = b.dataset.mode; $$('.tabs button').forEach(x => x.classList.toggle('on', x === b));
    $('#auth-btn').textContent = mode === 'login' ? 'Увійти' : 'Створити акаунт';
});
$('#auth-btn').onclick = doAuth;
$('#p').onkeydown = $('#u').onkeydown = e => { if (e.key === 'Enter') doAuth(); };
$('#logout').onclick = () => logout();
$('#rnd').onclick = genCities;
$('#coords').oninput = parseCoords;
$('#run').onclick = submit;
$('#dlg-close').onclick = () => $('#dlg').close();
addEventListener('resize', () => cities.length && draw($('#prev'), cities));
document.addEventListener('click', async e => {
    const c = e.target.closest('[data-cancel]'), o = e.target.closest('[data-open]');
    if (o) return openTask(o.dataset.open);
    if (c) {
        c.disabled = true;
        try { await api(`/tasks/${c.dataset.cancel}/cancel`, { method: 'POST' }); toast('Задачу скасовано'); await refresh(); }
        catch (x) { toast(x.message, 'er'); c.disabled = false; }
    }
});

token ? enter() : $('#auth').classList.remove('hidden');