'use strict';
/** 内存日志环（供 UI 展示）。 */
const MAX = 800;
const lines = [];

function ts() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

function log(msg) {
  lines.push(`[${ts()}] ${msg}`);
  if (lines.length > MAX) lines.shift();
  try { process.stdout.write('[DSWebAPI] ' + msg + '\n'); } catch (e) {}
}

function text(limit = 400) { return lines.slice(-limit).join('\n'); }
function clear() { lines.length = 0; }

module.exports = { log, text, clear };
