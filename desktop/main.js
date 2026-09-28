'use strict';
/**
 * DeepSeek Web API · Windows 桌面版（Electron 主进程）
 *
 * 稳定性要点：
 *  - 用 WebContentsView（主进程直接管理）承载官网，避免 <webview> 在隐藏容器中的黑屏/不加载问题。
 *  - 默认关闭硬件加速（可加 --gpu 开启），规避部分显卡导致的纯黑窗口。
 *  - 全程写日志到 userData/app.log，便于排查。
 */
if (!process.argv.includes('--gpu')) {
  try { require('electron').app.disableHardwareAcceleration(); } catch (e) {}
}

const { app, BrowserWindow, WebContentsView, Tray, Menu, ipcMain, nativeImage, nativeTheme, powerSaveBlocker, shell, clipboard, dialog } = require('electron');
const path = require('path');
const fs = require('fs');

const settings = require('./src/settings');
const logger = require('./src/log');
const BridgeController = require('./src/bridge-controller');
const ApiServer = require('./src/server');

const DS_URL = 'https://chat.deepseek.com/';

let mainWindow = null;
let dsView = null;
let tray = null;
let apiServer = null;
let bridgeJs = '';
let quitting = false;
let wakeBlockerId = -1;
let dsVisible = false;
let navHeight = 64;
let logFile = null;

const controller = new BridgeController((m) => logger.log(m));

/* ---------------- 日志落盘 ---------------- */
function initFileLog() {
  try {
    logFile = path.join(app.getPath('userData'), 'app.log');
    const stream = fs.createWriteStream(logFile, { flags: 'a' });
    stream.write(`\n===== 启动 ${new Date().toISOString()} electron=${process.versions.electron} chrome=${process.versions.chrome} =====\n`);
    const orig = logger.log;
    logger.log = (m) => { orig(m); try { stream.write(m + '\n'); } catch (e) {} };
  } catch (e) {}
}

process.on('uncaughtException', (e) => logger.log('主进程未捕获异常: ' + (e && e.stack || e)));
process.on('unhandledRejection', (e) => logger.log('未处理的 Promise: ' + (e && e.stack || e)));

/* ---------------- 原生 ↔ 页面通道 ---------------- */
const transport = {
  ready: () => !!(dsView && dsView.webContents && !dsView.webContents.isDestroyed()),
  eval: (code) => { if (transport.ready()) dsView.webContents.executeJavaScript(code, true).catch(() => {}); },
  inject: (code) => { if (transport.ready()) dsView.webContents.executeJavaScript(code, true).catch(() => {}); },
};

function loadBridgeJs() {
  try { return fs.readFileSync(path.join(__dirname, 'assets', 'bridge.js'), 'utf8'); }
  catch (e) { logger.log('加载 bridge.js 失败: ' + e.message); return ''; }
}

function injectBridge() {
  if (!transport.ready() || !bridgeJs) return;
  dsView.webContents.executeJavaScript(bridgeJs, true)
    .then(() => logger.log('bridge.js 注入成功'))
    .catch((e) => logger.log('bridge.js 注入失败: ' + e.message));
  applySiteTheme();
  setTimeout(() => controller.probe(), 1200);
}

/* ---------------- 窗口 + 官网视图 ---------------- */
function createWindow() {
  mainWindow = new BrowserWindow({
    width: 480,
    height: 880,
    minWidth: 400,
    minHeight: 620,
    title: 'DeepSeek Web API',
    backgroundColor: '#0e1116',
    webPreferences: {
      preload: path.join(__dirname, 'preload-host.js'),
      contextIsolation: true,
      nodeIntegration: false,
      spellcheck: false,
    },
  });
  mainWindow.setMenuBarVisibility(false);
  mainWindow.loadFile(path.join(__dirname, 'renderer', 'index.html'))
    .catch((e) => logger.log('加载界面失败: ' + e.message));
  mainWindow.webContents.on('did-finish-load', () => logger.log('控制台界面加载完成'));
  mainWindow.webContents.on('did-fail-load', (e, code, desc) => logger.log(`控制台界面加载失败 ${code} ${desc}`));
  mainWindow.webContents.on('render-process-gone', (e, details) => {
    logger.log('控制台界面进程异常: ' + (details && details.reason) + '，重载界面');
    try { mainWindow.reload(); } catch (err) {}
  });

  mainWindow.on('resize', () => relayout());
  mainWindow.on('maximize', () => relayout());
  mainWindow.on('unmaximize', () => relayout());
  mainWindow.on('close', (e) => {
    if (!quitting && settings.get('closeToTray')) {
      e.preventDefault();
      mainWindow.hide();
      logger.log('已最小化到托盘（服务继续运行）');
    }
  });
  mainWindow.on('closed', () => { mainWindow = null; });

  createDsView();
}

function createDsView() {
  try {
    if (typeof WebContentsView !== 'function') {
      logger.log('当前 Electron 不支持 WebContentsView，请使用 30+ 版本');
      return;
    }
    dsView = new WebContentsView({
      webPreferences: {
        preload: path.join(__dirname, 'preload-guest.js'),
        contextIsolation: true,
        nodeIntegration: false,
        backgroundThrottling: false,
      },
    });
    mainWindow.contentView.addChildView(dsView);
    const wc = dsView.webContents;
    wc.setUserAgent('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36');
    wc.setWindowOpenHandler(({ url }) => { shell.openExternal(url); return { action: 'deny' }; });
    wc.on('did-start-loading', () => logger.log('官网开始加载'));
    wc.on('did-finish-load', () => { logger.log('官网加载完成: ' + wc.getURL()); injectBridge(); });
    wc.on('did-fail-load', (e, code, desc, url, isMain) => {
      logger.log(`官网加载失败 code=${code} desc=${desc} url=${url}`);
      notifyRenderer('ui:ds-error', `加载失败：${desc} (${code})`);
    });
    wc.on('did-navigate', () => { setTimeout(() => controller.probe(), 1200); relayout(); });
    wc.on('did-navigate-in-page', () => setTimeout(() => controller.probe(), 800));
    wc.on('render-process-gone', (e, details) => {
      logger.log('官网页面进程异常: ' + (details && details.reason) + '，尝试重载');
      controller.failAll('PAGE_CRASHED');
      try { wc.reload(); } catch (err) {}
    });
    wc.on('unresponsive', () => logger.log('官网页面无响应'));
    wc.on('console-message', () => {});
    if (typeof wc.on === 'function') {
      wc.on('console-message', (e, level, message) => {
        const msg = typeof level === 'string' ? level : message; // Electron 33+ 为 details 对象
        if (msg && String(msg).includes('DSWB')) logger.log('JS: ' + msg);
      });
    }
    controller.attach(transport);
    dsVisible = false;
    relayout();
    wc.loadURL(DS_URL).catch((e) => logger.log('loadURL 失败: ' + e.message));
    logger.log('官网视图已创建');
  } catch (e) {
    logger.log('创建官网视图失败: ' + (e && e.stack || e));
    notifyRenderer('ui:ds-error', '创建官网视图失败：' + (e && e.message));
  }
}

function relayout() {
  if (!dsView || !mainWindow || mainWindow.isDestroyed()) return;
  try {
    const [w, h] = mainWindow.getContentSize();
    const height = Math.max(0, h - navHeight);
    if (dsVisible) dsView.setBounds({ x: 0, y: 0, width: Math.max(0, w), height });
    else dsView.setBounds({ x: 0, y: 0, width: 0, height: 0 });
  } catch (e) {}
}

function setDsVisible(v) {
  dsVisible = !!v;
  relayout();
  logger.log('对话页 ' + (dsVisible ? '显示' : '隐藏'));
}

function notifyRenderer(channel, payload) {
  if (mainWindow && !mainWindow.isDestroyed()) {
    try { mainWindow.webContents.send(channel, payload); } catch (e) {}
  }
}

function showWindow(tab) {
  if (!mainWindow || mainWindow.isDestroyed()) createWindow();
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
  if (tab && mainWindow.webContents) mainWindow.webContents.send('ui:switch-tab', tab);
}

/* ---------------- 托盘 ---------------- */
function buildTrayMenu() {
  return Menu.buildFromTemplate([
    { label: '显示主界面', click: () => showWindow('control') },
    { label: '打开对话页', click: () => showWindow('web') },
    { type: 'separator' },
    { label: `本机接口: ${apiServer ? apiServer.localUrl() : ''}`, click: () => clipboard.writeText(apiServer ? apiServer.localUrl() : '') },
    { label: `局域网接口: ${apiServer ? apiServer.lanUrl() : ''}`, click: () => clipboard.writeText(apiServer ? apiServer.lanUrl() : '') },
    { type: 'separator' },
    { label: '退出', click: () => { quitting = true; app.quit(); } },
  ]);
}

function createTray() {
  try {
    let img = nativeImage.createFromPath(path.join(__dirname, 'assets', 'icon.png'));
    if (img.isEmpty()) img = nativeImage.createEmpty();
    tray = new Tray(img.resize({ width: 16, height: 16 }));
    tray.setToolTip('DeepSeek Web API · 运行中');
    tray.setContextMenu(buildTrayMenu());
    tray.on('double-click', () => showWindow('control'));
  } catch (e) {
    logger.log('托盘创建失败: ' + e.message);
  }
}

/* ---------------- HTTP 服务 ---------------- */
function startServer() {
  if (apiServer) apiServer.stop();
  apiServer = new ApiServer({ controller, settings, log: (m) => logger.log(m) });
  apiServer.onStats = () => pushStatus();
  apiServer.start();
  if (tray) tray.setContextMenu(buildTrayMenu());
}

function statusSnapshot() {
  const s = settings.all();
  return {
    running: true,
    bridgeAttached: controller.isAttached(),
    pageReady: apiServer ? apiServer.stats.lastProbeReady : false,
    loggedIn: apiServer ? apiServer.stats.lastProbeLogin : false,
    localUrl: apiServer ? apiServer.localUrl() : '',
    lanUrl: apiServer ? apiServer.lanUrl() : '',
    port: apiServer ? apiServer.port : s.port,
    apiKey: s.apiKey,
    stats: apiServer ? apiServer.stats : { total: 0, ok: 0, inflight: 0, lastCall: '—' },
    settings: s,
    log: logger.text(300),
    dsUrl: dsView ? dsView.webContents.getURL() : '',
    versions: { electron: process.versions.electron, chrome: process.versions.chrome, node: process.versions.node },
    logFile,
  };
}

function pushStatus() { notifyRenderer('ui:status', statusSnapshot()); }

/* ---------------- 系统集成 ---------------- */
function applyAutoLaunch(on) {
  try { app.setLoginItemSettings({ openAtLogin: !!on, args: ['--hidden'] }); } catch (e) {}
}
function applyKeepAwake(on) {
  try {
    if (on) {
      if (wakeBlockerId < 0 || !powerSaveBlocker.isStarted(wakeBlockerId)) {
        wakeBlockerId = powerSaveBlocker.start('prevent-app-suspension');
      }
    } else if (wakeBlockerId >= 0) { powerSaveBlocker.stop(wakeBlockerId); wakeBlockerId = -1; }
  } catch (e) {}
}
function applySiteTheme() {
  try { controller.setTheme(nativeTheme.shouldUseDarkColors); } catch (e) {}
}

/* ---------------- IPC ---------------- */
function registerIpc() {
  ipcMain.handle('api:status', () => statusSnapshot());
  ipcMain.handle('api:settings:set', (e, patch) => {
    const before = settings.all();
    settings.update(patch || {});
    const after = settings.all();
    if (after.port !== before.port || after.bindLan !== before.bindLan) {
      logger.log('端口/绑定方式变更，重启 HTTP 服务…');
      startServer();
    }
    if (after.autoLaunch !== before.autoLaunch) applyAutoLaunch(after.autoLaunch);
    if (after.keepAwake !== before.keepAwake) applyKeepAwake(after.keepAwake);
    controller.configure(after.thinking, after.search);
    pushStatus();
    return statusSnapshot();
  });
  ipcMain.on('ui:tab', (e, tab, navH) => {
    if (typeof navH === 'number' && navH > 0) navHeight = Math.round(navH);
    setDsVisible(tab === 'web');
  });
  ipcMain.on('api:action', (e, action) => {
    logger.log('操作: ' + action);
    if (action === 'reload') { if (transport.ready()) dsView.webContents.reload(); }
    else if (action === 'newChat') controller.newChat().then((r) => logger.log('新建对话: ' + (r && r.ok ? '成功' : '失败')));
    else if (action === 'clearLog') logger.clear();
    else if (action === 'openAppDevtools' && mainWindow) mainWindow.webContents.openDevTools({ mode: 'detach' });
    else if (action === 'openDsDevtools' && transport.ready()) dsView.webContents.openDevTools({ mode: 'detach' });
    else if (action === 'copyLocal') clipboard.writeText(apiServer ? apiServer.localUrl() : '');
    else if (action === 'copyLan') clipboard.writeText(apiServer ? apiServer.lanUrl() : '');
    else if (action === 'openSite') shell.openExternal(DS_URL);
    else if (action === 'openLog') { try { shell.showItemInFolder(logFile); } catch (err) {} }
    pushStatus();
  });
  ipcMain.on('dsb:event', (e, json) => { try { controller.onJsEvent(json); } catch (err) {} });
  ipcMain.handle('ui:probe', () => { controller.probe(); return true; });
}

/* ---------------- 启动 ---------------- */
function boot() {
  try {
    settings.init(app.getPath('userData'));
    initFileLog();
    logger.log(`userData=${app.getPath('userData')} gpu=${!process.argv.includes('--gpu') ? 'off' : 'on'}`);
    bridgeJs = loadBridgeJs();
    registerIpc();
    controller.attach(transport);
    startServer();
    createWindow();
    createTray();
    applyAutoLaunch(settings.get('autoLaunch'));
    applyKeepAwake(settings.get('keepAwake'));
    if (process.argv.includes('--hidden') && mainWindow) mainWindow.hide();
    nativeTheme.on('updated', () => applySiteTheme());
    setInterval(() => { if (controller.isAttached()) controller.probe(); }, 30000);
    setInterval(() => pushStatus(), 3000);
    app.on('activate', () => showWindow('control'));
  } catch (e) {
    logger.log('启动失败: ' + (e && e.stack || e));
    try { dialog.showErrorBox('DeepSeek Web API 启动失败', String(e && e.message || e)); } catch (err) {}
  }
}

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => showWindow('control'));
  app.whenReady().then(boot);
  app.on('window-all-closed', () => { if (!settings.get('closeToTray')) { quitting = true; app.quit(); } });
  app.on('before-quit', () => { quitting = true; if (apiServer) apiServer.stop(); });
}
