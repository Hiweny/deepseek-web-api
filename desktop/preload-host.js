'use strict';
/** 主窗口渲染层的桥（contextBridge）。 */
const { contextBridge, ipcRenderer } = require('electron');
const path = require('path');
const { pathToFileURL } = require('url');

contextBridge.exposeInMainWorld('host', {
  status: () => ipcRenderer.invoke('api:status'),
  setSettings: (patch) => ipcRenderer.invoke('api:settings:set', patch),
  action: (a) => ipcRenderer.send('api:action', a),
  probe: () => ipcRenderer.invoke('ui:probe'),
  tab: (t, navHeight) => ipcRenderer.send('ui:tab', t, navHeight),
  onStatus: (cb) => ipcRenderer.on('ui:status', (e, s) => cb(s)),
  onSwitchTab: (cb) => ipcRenderer.on('ui:switch-tab', (e, tab) => cb(tab)),
  onDsError: (cb) => ipcRenderer.on('ui:ds-error', (e, msg) => cb(msg)),
  guestPreload: pathToFileURL(path.join(__dirname, 'preload-guest.js')).toString(),
});
