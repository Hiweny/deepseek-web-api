'use strict';
/** 设置持久化（userData/config.json）。 */
const fs = require('fs');
const path = require('path');

const DEFAULTS = {
  port: 8787,
  apiKey: 'sk-deepseek',
  bindLan: true,          // true=监听 0.0.0.0（局域网可用）, false=仅 127.0.0.1
  stateless: false,       // 每次发送完整对话
  thinking: 'auto',       // auto | on | off
  search: 'auto',         // auto | on | off
  timeout: 300,           // 单次回复超时（秒）
  closeToTray: true,      // 关闭窗口时最小化到托盘
  autoLaunch: false,      // 开机自启
  keepAwake: true,        // 阻止系统休眠（保活）
};

let dir = null;
let cache = Object.assign({}, DEFAULTS);

function file() { return path.join(dir || '.', 'config.json'); }

function init(userDataDir) {
  dir = userDataDir;
  try {
    const raw = fs.readFileSync(file(), 'utf8');
    cache = Object.assign({}, DEFAULTS, JSON.parse(raw));
  } catch (e) {
    cache = Object.assign({}, DEFAULTS);
  }
  return cache;
}

function save() {
  try {
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(file(), JSON.stringify(cache, null, 2), 'utf8');
  } catch (e) { /* ignore */ }
}

module.exports = {
  DEFAULTS,
  init,
  all: () => cache,
  get: (k) => cache[k],
  set: (k, v) => { cache[k] = v; save(); },
  update: (obj) => { Object.assign(cache, obj); save(); },
};
