'use strict';
/** 官网页面（webview guest）的桥：把 bridge.js 的事件回传给主进程。 */
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('DSB', {
  onEvent: (json) => {
    try { ipcRenderer.send('dsb:event', String(json)); } catch (e) {}
  },
});
