'use strict';
/** 本地 OpenAI 兼容 HTTP 服务（Node http，无第三方依赖）。 */
const http = require('http');
const os = require('os');
const adapter = require('./adapter');
const promptBuilder = require('./prompt');

const HOLD = 32; // 流式工具标签检测时保留的尾部字符数

function errJson(msg, type) {
  return JSON.stringify({ error: { message: msg, type: type || 'invalid_request_error', code: type || 'invalid_request_error' } });
}

function nowSec() { return Math.floor(Date.now() / 1000); }

class ApiServer {
  constructor(opts) {
    this.controller = opts.controller;
    this.settings = opts.settings;
    this.log = opts.log;
    this.server = null;
    this.stats = { total: 0, ok: 0, inflight: 0, lastCall: '—', lastProbeLogin: false, lastProbeReady: false };
    this._queue = Promise.resolve();
    this._port = 0;
  }

  _enqueue(fn) {
    const run = this._queue.then(fn, fn);
    this._queue = run.catch(() => {});
    return run;
  }

  start() {
    const port = Number(this.settings.get('port')) || 8787;
    const host = this.settings.get('bindLan') ? '0.0.0.0' : '127.0.0.1';
    this.server = http.createServer((req, res) => this._handle(req, res));
    this.server.on('error', (e) => {
      this.log('HTTP 服务错误: ' + e.message);
    });
    this.server.listen(port, host, () => {
      this._port = this.server.address().port;
      this.log(`HTTP 服务已启动: http://${host}:${this._port} (局域网: http://${lanIp()}:${this._port})`);
    });
  }

  stop() {
    if (this.server) { try { this.server.close(); } catch (e) {} this.server = null; }
  }

  get port() { return this._port || Number(this.settings.get('port')) || 8787; }
  lanUrl() { return `http://${lanIp()}:${this.port}/v1`; }
  localUrl() { return `http://127.0.0.1:${this.port}/v1`; }

  _cors(res) {
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Authorization, Content-Type, *');
  }

  _json(res, code, obj) {
    const body = typeof obj === 'string' ? obj : JSON.stringify(obj);
    this._cors(res);
    res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(body) });
    res.end(body);
  }

  _auth(req, res) {
    const key = this.settings.get('apiKey');
    if (!key) return true;
    const h = req.headers['authorization'] || '';
    const got = h.toLowerCase().startsWith('bearer ') ? h.slice(7).trim() : h.trim();
    if (got === key) return true;
    this._json(res, 401, errJson('Incorrect API key provided.', 'invalid_request_error'));
    return false;
  }

  _readBody(req) {
    return new Promise((resolve) => {
      const chunks = [];
      let size = 0;
      req.on('data', (c) => {
        size += c.length;
        if (size > 64 * 1024 * 1024) { req.destroy(); resolve(''); return; }
        chunks.push(c);
      });
      req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
      req.on('error', () => resolve(''));
    });
  }

  async _handle(req, res) {
    const url = req.url || '/';
    const path = url.split('?')[0];
    const method = (req.method || 'GET').toUpperCase();

    if (method === 'OPTIONS') { this._cors(res); res.writeHead(204); res.end(); return; }

    try {
      if (path === '/' || path === '/health' || path === '/v1' || path === '/v1/') return this._health(res);
      if (path === '/v1/models' || path === '/models') {
        if (!this._auth(req, res)) return;
        return this._json(res, 200, adapter.modelsList());
      }
      if (path === '/v1/chat/completions' || path === '/chat/completions' || path === '/v1/completions') {
        if (!this._auth(req, res)) return;
        if (method !== 'POST') return this._json(res, 405, errJson('Method not allowed'));
        return this._chat(req, res);
      }
      return this._json(res, 404, errJson('Not found: ' + path));
    } catch (e) {
      this.log('请求处理异常: ' + e.message);
      try { this._json(res, 500, errJson(e.message, 'server_error')); } catch (e2) {}
    }
  }

  _health(res) {
    this._json(res, 200, {
      status: 'ok',
      service: 'deepseek-web-api-desktop',
      bridge_attached: this.controller.isAttached(),
      logged_in: this.stats.lastProbeLogin,
      page_ready: this.stats.lastProbeReady,
      inflight: this.stats.inflight,
      total_calls: this.stats.total,
      ok_calls: this.stats.ok,
      endpoint: this.localUrl(),
      lan_endpoint: this.lanUrl(),
    });
  }

  _chat(req, res) {
    this._readBody(req).then((raw) => {
      let body;
      try { body = JSON.parse(raw || '{}'); }
      catch (e) { return this._json(res, 400, errJson('Invalid JSON body')); }

      if (!this.controller.isAttached()) {
        return this._json(res, 503, errJson('网页桥未就绪：请保持程序运行并在「对话页」登录 DeepSeek 官网。', 'server_error'));
      }

      const model = body.model || 'deepseek';
      const stream = !!body.stream;
      const stateless = !!this.settings.get('stateless');

      promptBuilder.build(body, stateless).then((pb) => {
        if (!pb.text.trim() && !pb.attachments.length) {
          return this._json(res, 400, errJson('messages 为空'));
        }

        let thinking = this.settings.get('thinking') || 'auto';
        const search = this.settings.get('search') || 'auto';
        const re = body.reasoning_effort || '';
        if (String(re).toLowerCase() === 'none') thinking = 'off';
        else if (re) thinking = 'on';
        else if (/reasoner|think/i.test(model)) thinking = 'on';
        this.controller.configure(thinking, search);

        this.stats.total++;
        this.stats.inflight++;
        const t0 = Date.now();
        if (this.onStats) this.onStats();

        this._enqueue(async () => {
          try {
            for (const a of pb.attachments) {
              const r = await this.controller.attachFile(a.name, a.mime, a.base64, 150);
              if (!r || !r.ok) this.log('附件挂载失败: ' + a.name + ' ' + (r && r.error));
            }
            if (stream) await this._stream(model, pb, res);
            else await this._blocking(model, pb, res);
            this.stats.ok++;
            this.stats.lastCall = `成功 · ${((Date.now() - t0) / 1000).toFixed(1)}s` + (pb.attachments.length ? ` · ${pb.attachments.length}附件` : '');
          } catch (e) {
            this.log('chat 处理异常: ' + e.message);
            this.stats.lastCall = '异常: ' + e.message;
            try { if (!res.headersSent) this._json(res, 500, errJson(e.message, 'server_error')); else res.end(); } catch (e2) {}
          } finally {
            this.stats.inflight--;
            if (this.onStats) this.onStats();
          }
        });
      });
    });
  }

  async _blocking(model, pb, res) {
    const timeout = Number(this.settings.get('timeout')) || 300;
    const r = await this.controller.sendPrompt(pb.text, null, timeout);
    const thinking = (r && r.thinking) || '';
    const content = (r && r.content) || '';
    if (!content && !thinking) {
      const err = (r && r.error) || 'EMPTY';
      this.stats.lastCall = '失败: ' + err;
      return this._json(res, 502, errJson('DeepSeek 未返回内容: ' + err, 'server_error'));
    }
    const cr = adapter.process(thinking, content);
    const out = adapter.buildCompletion(adapter.newId(), model, nowSec(), cr.thinking, cr.content, cr.toolCalls, cr.finishReason,
      adapter.estTokens(pb.text), adapter.estTokens(cr.content + cr.thinking));
    this._json(res, 200, out);
  }

  _stream(model, pb, res) {
    return new Promise((resolve) => {
      const id = adapter.newId();
      const created = nowSec();
      const timeout = Number(this.settings.get('timeout')) || 300;

      this._cors(res);
      res.writeHead(200, {
        'Content-Type': 'text/event-stream; charset=utf-8',
        'Cache-Control': 'no-cache',
        'Connection': 'keep-alive',
        'X-Accel-Buffering': 'no',
      });

      let writeOk = true;
      const send = (obj) => { if (!writeOk) return; try { res.write('data: ' + JSON.stringify(obj) + '\n\n'); } catch (e) { writeOk = false; } };

      send(adapter.chunk(id, model, created, { role: 'assistant', content: '' }, null));

      const ping = setInterval(() => { if (writeOk) { try { res.write(': ping\n\n'); } catch (e) {} } }, 15000);

      let emitted = 0, lastTh = 0, toolMode = false, finished = false;

      const finishUp = (payload) => {
        if (finished) return;
        finished = true;
        clearInterval(ping);
        if (writeOk) { try { res.write('data: [DONE]\n\n'); } catch (e) {} }
        try { res.end(); } catch (e) {}
        this.stats.lastCall = payload;
        if (this.onStats) this.onStats();
        resolve();
      };

      reqAbort(res, () => { writeOk = false; if (!finished) { finished = true; clearInterval(ping); try { res.end(); } catch (e) {} resolve(); } });

      this.controller.sendPromptStream(pb.text, null, timeout, {
        onDelta: (th, ct) => {
          if (finished || !writeOk) return;
          if (th.length > lastTh) {
            const piece = th.slice(lastTh);
            lastTh = th.length;
            send(adapter.chunk(id, model, created, { reasoning_content: piece }, null));
          }
          if (toolMode) return;
          const norm = adapter.normTag(ct);
          const si = norm.indexOf('<|tool_calls_begin|>');
          if (si >= 0) {
            if (si > emitted) send(adapter.chunk(id, model, created, { content: ct.slice(emitted, si) }, null));
            emitted = si;
            toolMode = true;
          } else {
            const safe = ct.length - HOLD;
            if (safe > emitted) {
              send(adapter.chunk(id, model, created, { content: ct.slice(emitted, safe) }, null));
              emitted = safe;
            }
          }
        },
        onFinish: (r) => {
          if (finished) return;
          const content = (r && r.content) || '';
          const thinking = (r && r.thinking) || '';
          const ok = (r && r.ok) || content || thinking;
          if (!ok) {
            send(adapter.chunk(id, model, created, { content: '\n[错误] ' + ((r && r.error) || 'UNKNOWN') }, null));
            send(adapter.chunk(id, model, created, {}, 'stop'));
            return finishUp('失败: ' + ((r && r.error) || 'UNKNOWN'));
          }
          const cr = adapter.process(thinking, content);
          if (cr.toolCalls && cr.toolCalls.length) {
            const tcs = adapter.toOpenAiToolCalls(cr.toolCalls);
            tcs.forEach((tc, i) => {
              send(adapter.chunk(id, model, created, { tool_calls: [{ index: i, id: tc.id, type: 'function', function: tc.function }] }, null));
            });
            send(adapter.chunk(id, model, created, {}, 'tool_calls'));
            return finishUp('成功 · 工具调用 x' + tcs.length);
          }
          if (content.length > emitted) send(adapter.chunk(id, model, created, { content: content.slice(emitted) }, null));
          send(adapter.chunk(id, model, created, {}, 'stop'));
          finishUp('成功 · ' + content.length + '字' + (r && r.recalled ? ' · 防撤回' : ''));
        },
      });
    });
  }

  onProbe(o) {
    if (o && typeof o.loggedIn === 'boolean') this.stats.lastProbeLogin = o.loggedIn;
    if (o && typeof o.ready === 'boolean') this.stats.lastProbeReady = o.ready;
    if (this.onStats) this.onStats();
  }
}

function reqAbort(res, cb) {
  try { res.req.on('close', cb); } catch (e) {}
}

function lanIp() {
  const ifs = os.networkInterfaces();
  for (const name of Object.keys(ifs)) {
    for (const it of ifs[name] || []) {
      if (it.family === 'IPv4' && !it.internal && !/^169\.254\./.test(it.address)) return it.address;
    }
  }
  return '127.0.0.1';
}

module.exports = ApiServer;
module.exports.lanIp = lanIp;
