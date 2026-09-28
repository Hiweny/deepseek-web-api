'use strict';
const FIELD_DEFS = [
  { key: 'port', label: '端口', hint: 'HTTP 监听端口（改后自动重启服务）', type: 'number' },
  { key: 'apiKey', label: 'API Key', hint: '外部调用需携带的密钥', type: 'text' },
  { key: 'timeout', label: '超时（秒）', hint: '单次回复等待上限', type: 'number' },
  { key: 'thinking', label: '思考策略', hint: '跟随官网 / 强制开 / 强制关', type: 'cycle', values: ['auto', 'on', 'off'] },
  { key: 'search', label: '搜索策略', hint: '跟随官网 / 强制开 / 强制关', type: 'cycle', values: ['auto', 'on', 'off'] },
];

const SWITCH_DEFS = [
  { key: 'bindLan', label: '允许局域网访问', hint: '监听 0.0.0.0，同一网络的设备可调用' },
  { key: 'stateless', label: '无状态模式', hint: '每次发送完整对话，不依赖官网会话记忆' },
  { key: 'autoLaunch', label: '开机自启', hint: '登录 Windows 后自动在后台启动' },
  { key: 'closeToTray', label: '关闭到托盘', hint: '点右上角 × 不退出，继续后台运行' },
  { key: 'keepAwake', label: '阻止系统休眠', hint: '提高长时间运行稳定性' },
];

const $ = (id) => document.getElementById(id);
let lastSettings = null;
let currentTab = 'control';

/* ---------- 标签切换 ---------- */
function tabOf() {
  const a = document.querySelector('.nav-btn.active');
  return a ? a.dataset.tab : 'control';
}

function syncTabToMain() {
  try {
    const nav = $('nav');
    const h = nav ? Math.round(nav.getBoundingClientRect().height) : 64;
    window.host.tab(currentTab, h);
  } catch (e) {}
}

function switchTab(tab) {
  currentTab = tab;
  document.querySelectorAll('.panel').forEach((p) => p.classList.remove('active'));
  document.querySelectorAll('.nav-btn').forEach((b) => b.classList.toggle('active', b.dataset.tab === tab));
  $(tab === 'web' ? 'panel-web' : 'panel-control').classList.add('active');
  syncTabToMain();
}

/* ---------- 提示条 ---------- */
function showBanner(msg) {
  const b = $('banner');
  if (!b) return;
  if (!msg) { b.classList.add('hidden'); b.textContent = ''; return; }
  b.textContent = msg;
  b.classList.remove('hidden');
}

/* ---------- 设置渲染 ---------- */
function renderSettings(s) {
  if (lastSettings && JSON.stringify(lastSettings) === JSON.stringify(s)) return;
  lastSettings = Object.assign({}, s);
  const box = $('settings-rows');
  box.innerHTML = '';
  for (const f of FIELD_DEFS) {
    const row = document.createElement('div');
    row.className = 'field';
    const col = document.createElement('div');
    col.className = 'fcol';
    col.innerHTML = `<div class="flabel">${f.label}</div><div class="fhint">${f.hint}</div>`;
    row.appendChild(col);
    if (f.type === 'cycle') {
      const btn = document.createElement('button');
      btn.className = 'cycle';
      btn.textContent = s[f.key];
      btn.onclick = async () => {
        const i = f.values.indexOf(s[f.key]);
        const next = f.values[(i + 1) % f.values.length];
        await window.host.setSettings({ [f.key]: next });
        lastSettings = null;
        refresh();
      };
      row.appendChild(btn);
    } else {
      const input = document.createElement('input');
      input.type = f.type;
      input.value = s[f.key];
      input.onchange = async () => {
        let v = input.value.trim();
        if (f.type === 'number') v = parseInt(v, 10) || s[f.key];
        await window.host.setSettings({ [f.key]: v });
        lastSettings = null;
        refresh();
      };
      row.appendChild(input);
    }
    box.appendChild(row);
  }
  for (const sw of SWITCH_DEFS) {
    const row = document.createElement('div');
    row.className = 'field';
    const col = document.createElement('div');
    col.className = 'fcol';
    col.innerHTML = `<div class="flabel">${sw.label}</div><div class="fhint">${sw.hint}</div>`;
    row.appendChild(col);
    const label = document.createElement('label');
    label.className = 'switch';
    label.innerHTML = `<input type="checkbox" ${s[sw.key] ? 'checked' : ''}><span class="slider"></span>`;
    label.querySelector('input').onchange = async (e) => {
      await window.host.setSettings({ [sw.key]: e.target.checked });
      lastSettings = null;
      refresh();
    };
    row.appendChild(label);
    box.appendChild(row);
  }
}

/* ---------- 状态刷新 ---------- */
function applyStatus(st) {
  if (!st) return;
  $('st-service').textContent = st.running ? '运行中' : '未运行';
  $('st-service').style.color = 'var(--green)';
  $('st-page').textContent = st.bridgeAttached ? (st.pageReady ? '已就绪' : '加载中…') : '未初始化';
  $('st-login').textContent = st.loggedIn ? '已登录' : '未登录（请到「对话页」登录）';
  $('st-login').style.color = st.loggedIn ? 'var(--green)' : 'var(--amber)';
  $('st-calls').textContent = `总 ${st.stats.total} 次（成功 ${st.stats.ok}）` + (st.stats.inflight ? ` · 进行中 ${st.stats.inflight}` : '');
  $('st-last').textContent = st.stats.lastCall || '—';
  $('st-local').textContent = st.localUrl || '—';
  $('st-lan').textContent = st.lanUrl || '—';
  $('st-key').textContent = st.apiKey || '—';
  if (st.versions) $('st-ver').textContent = `v${st.versions.electron} / Chrome ${st.versions.chrome}`;
  $('st-dsurl').textContent = st.dsUrl || '未加载';
  $('log').textContent = st.log || '';
  const ph = $('ph-hint');
  if (ph) ph.textContent = st.dsUrl && st.dsUrl !== 'about:blank' ? '已加载：' + st.dsUrl : '正在加载…（若长时间空白，请点控制台的「重载官网」）';
  renderSettings(st.settings || {});
}

async function refresh() {
  try { applyStatus(await window.host.status()); } catch (e) {}
}

/* ---------- 绑定 ---------- */
function bind() {
  document.querySelectorAll('.nav-btn').forEach((b) => { b.onclick = () => switchTab(b.dataset.tab); });
  document.querySelectorAll('[data-action]').forEach((b) => {
    b.onclick = () => { window.host.action(b.dataset.action); setTimeout(refresh, 300); };
  });
  if (window.host.onStatus) window.host.onStatus(applyStatus);
  if (window.host.onSwitchTab) window.host.onSwitchTab((tab) => switchTab(tab));
  if (window.host.onDsError) window.host.onDsError((msg) => { showBanner('官网加载失败：' + msg); });
  window.addEventListener('resize', syncTabToMain);
}

window.addEventListener('DOMContentLoaded', () => {
  bind();
  refresh();
  setTimeout(syncTabToMain, 300);
  setInterval(refresh, 3000);
});
