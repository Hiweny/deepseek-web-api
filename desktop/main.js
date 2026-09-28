'use strict';
/**
 * DeepSeek Web API · Windows 桌面版（Electron 主进程）
 * 职责：主窗口（控制台 + 内嵌官网 webview）、托盘保活、本地 HTTP 服务、原生↔页面通道。
 */
const { app, BrowserWindow, Tray, Menu, ipcMain, nativeImage, nativeTheme, powerSaveBlocker, shell, clipboard } = require('electron');
const path = require('path');
const fs = require('fs');

const settings = require('./src/settings');
const logger = require('./src/log');
const BridgeController = require('./src/bridge-controller');
const ApiServer = require('./src/server');

const DS_URL = 'https://chat.deepseek.com/';

let mainWindow = null;
let tray = null;
let guestWC = null;
let bridgeJs = '';
let apiServer = null;
let quitting = false;
let wakeBlockerId = -1;

const controller = new BridgeController((m) => logger.log(m));

/* ---------------- 原生 ↔ 页面通道 ---------------- */
const transport = {
  ready: () => !!(guestWC && !guestWC.isDestroyed()),
  eval: (code) => { if (transport.ready()) { guestWC.executeJavaScript(code, true).catch(() => {}); } },
  inject: (code) => { if (transport.ready()) { guestWC.executeJavaScript(code, true).catch(() => {}); } },
};

function loadBridgeJs() {
  try { return fs.readFileSync(path.join(__dirname, 'assets', 'bridge.js'), 'utf8'); }
  catch (e) { logger.log('加载 bridge.js 失败: ' + e.message); return ''; }
}

function injectBridge() {
  if (!guestWC || guestWC.isDestroyed() || !bridgeJs) return;
  guestWC.executeJavaScript(bridgeJs, true).catch((e) => logger.log('注入失败: ' + e.message));
  applySiteTheme();
  setTimeout(() => controller.probe(), 1200);
}

/* ---------------- 窗口 ---------------- */
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
      webviewTag: true,
      spellcheck: false,
    },
  });
  mainWindow.setMenuBarVisibility(false);
  mainWindow.loadFile(path.join(__dirname, 'renderer', 'index.html'));

  mainWindow.on('close', (e) => {
    if (!quitting && settings.get('closeToTray')) {
      e.preventDefault();
      mainWindow.hide();
      logger.log('已最小化到托盘（服务继续运行）');
    }
  });
  mainWindow.on('closed', () => { mainWindow = null; });
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
  };
}

function pushStatus() {
  if (mainWindow && !mainWindow.isDestroyed()) mainWindow.webContents.send('ui:status', statusSnapshot());
}

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
    } else if (wakeBlockerId >= 0) {
      powerSaveBlocker.stop(wakeBlockerId);
      wakeBlockerId = -1;
    }
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
  ipcMain.on('api:action', (e, action) => {
    logger.log('操作: ' + action);
    if (action === 'reload') { if (guestWC && !guestWC.isDestroyed()) guestWC.reload(); }
    else if (action === 'newChat') controller.newChat().then((r) => logger.log('新建对话: ' + (r && r.ok ? '成功' : '失败')));
    else if (action === 'clearLog') logger.clear();
    else if (action === 'openDevtools' && guestWC) guestWC.openDevTools({ mode: 'detach' });
    else if (action === 'copyLocal') clipboard.writeText(apiServer ? apiServer.localUrl() : '');
    else if (action === 'copyLan') clipboard.writeText(apiServer ? apiServer.lanUrl() : '');
    else if (action === 'openSite') shell.openExternal(DS_URL);
    pushStatus();
  });
  ipcMain.on('dsb:event', (e, json) => {
    try { controller.onJsEvent(json); } catch (err) {}
  });
  ipcMain.handle('ui:probe', () => { controller.probe(); return true; });
}

/* ---------------- 生命周期 ---------------- */
const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => showWindow('control'));

  app.on('web-contents-created', (event, wc) => {
    if (wc.getType() === 'webview') {
      guestWC = wc;
      logger.log('捕获到官网 webview');
      controller.attach(transport);
      try { wc.setWindowOpenHandler(({ url }) => { shell.openExternal(url); return { action: 'deny' }; }); } catch (e) {}
      wc.on('did-finish-load', () => { logger.log('页面加载完成: ' + wc.getURL()); injectBridge(); });
      wc.on('did-navigate', () => setTimeout(() => controller.probe(), 1200));
      wc.on('did-navigate-in-page', () => setTimeout(() => controller.probe(), 800));
      wc.on('render-process-gone', (e, details) => {
        logger.log('页面进程异常: ' + (details && details.reason) + '，尝试重载');
        controller.failAll('PAGE_CRASHED');
        try { wc.reload(); } catch (err) {}
      });
      wc.on('destroyed', () => { if (guestWC === wc) guestWC = null; });
    }
  });

  app.whenReady().then(() => {
    settings.init(app.getPath('userData'));
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
    // 看门狗：定期探测页面、刷新状态
    setInterval(() => { if (controller.isAttached()) controller.probe(); }, 30000);
    setInterval(() => pushStatus(), 3000);

    app.on('activate', () => showWindow('control'));
  });

  app.on('window-all-closed', () => {
    if (!settings.get('closeToTray')) { quitting = true; app.quit(); }
  });

  app.on('before-quit', () => { quitting = true; if (apiServer) apiServer.stop(); });
}
