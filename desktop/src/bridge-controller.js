'use strict';
/**
 * 原生(主进程) ↔ 页面(bridge.js) 唯一通道：用 reqId 关联请求与事件，支持流式回调。
 * transport 需提供 { ready(), eval(code), inject(code) }。
 */
const crypto = require('crypto');

function jsArg(o) {
  return JSON.stringify(o).replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');
}

class BridgeController {
  constructor(logger) {
    this.log = logger || (() => {});
    this.transport = null;
    this.pending = new Map();   // reqId -> {resolve, timer}
    this.streams = new Map();   // reqId -> {onDelta, onFinish, timer}
  }

  attach(transport) { this.transport = transport; }
  isAttached() { return !!(this.transport && this.transport.ready()); }

  _eval(code) {
    if (this.transport) { try { this.transport.eval(code); } catch (e) {} }
  }

  injectBridge(js) { if (this.transport) { try { this.transport.inject(js); } catch (e) {} } }
  probe() { this._eval('window.DSKB && DSKB.probe();'); }
  configure(thinking, search) { this._eval(`window.DSKB && DSKB.configure(${jsArg({ thinking, search })});`); }
  setTheme(dark) { this._eval(`window.DSKB && DSKB.setTheme(${dark ? 'true' : 'false'});`); }

  _newReqId() { return crypto.randomBytes(8).toString('hex'); }

  _invoke(method, arg, reqId) {
    const code = `window.DSKB ? DSKB.${method}(${jsArg(arg)}) : (function(){try{DSB.onEvent(JSON.stringify({type:'${method}',reqId:'${reqId}',ok:false,error:'NO_BRIDGE'}))}catch(e){}})();`;
    this._eval(code);
  }

  /** 同步（Promise）调用某个 bridge 方法并等待结果。 */
  callJs(method, arg, timeoutSec) {
    return new Promise((resolve) => {
      if (!this.isAttached()) { resolve({ ok: false, error: 'APP_NOT_RUNNING:请保持程序运行且已登录 DeepSeek 官网' }); return; }
      const reqId = this._newReqId();
      const timer = setTimeout(() => {
        if (this.pending.delete(reqId)) resolve({ ok: false, error: 'TIMEOUT' });
      }, Math.max(5, timeoutSec) * 1000);
      this.pending.set(reqId, { resolve, timer });
      this._invoke(method, Object.assign({}, arg, { reqId }), reqId);
    });
  }

  sendPrompt(prompt, sessionId, timeoutSec) {
    const arg = { prompt };
    if (sessionId) arg.sessionId = sessionId;
    return this.callJs('send', arg, timeoutSec);
  }

  /** 流式发送：立即返回 reqId，事件经 listener 回调。 */
  sendPromptStream(prompt, sessionId, timeoutSec, listener) {
    if (!this.isAttached()) {
      if (listener && listener.onFinish) listener.onFinish({ ok: false, error: 'APP_NOT_RUNNING' });
      return null;
    }
    const reqId = this._newReqId();
    const timer = setTimeout(() => {
      const s = this.streams.get(reqId);
      if (s) {
        this.streams.delete(reqId);
        if (s.onFinish) s.onFinish({ ok: false, error: 'TIMEOUT' });
      }
    }, (Math.max(5, timeoutSec) + 5) * 1000);
    this.streams.set(reqId, {
      onDelta: listener && listener.onDelta,
      onFinish: (r) => { clearTimeout(timer); if (listener && listener.onFinish) listener.onFinish(r); },
      timer,
    });
    const arg = { prompt, reqId };
    if (sessionId) arg.sessionId = sessionId;
    this._invoke('send', arg, reqId);
    return reqId;
  }

  attachFile(name, mime, base64, timeoutSec) {
    return this.callJs('attachFile', { name, mime, b64: base64 }, timeoutSec);
  }

  newChat() { return this.callJs('newChat', {}, 20); }

  /** 处理来自页面的事件。 */
  onJsEvent(json) {
    let o;
    try { o = JSON.parse(json); } catch (e) { return; }
    const type = o.type;
    const reqId = o.reqId || '';

    if (type === 'delta') {
      const s = reqId ? this.streams.get(reqId) : null;
      if (s && s.onDelta) { try { s.onDelta(o.thinking || '', o.content || '', !!o.recalled); } catch (e) {} }
      return;
    }

    if (type === 'reply') {
      const p = reqId ? this.pending.get(reqId) : null;
      if (p) { clearTimeout(p.timer); this.pending.delete(reqId); p.resolve(o); }
      const s = reqId ? this.streams.get(reqId) : null;
      if (s) { this.streams.delete(reqId); if (s.onFinish) s.onFinish(o); }
      this.log(`回复 ok=${!!o.ok}${o.recalled ? ' [撤回已拦截]' : ''} 正文=${(o.content || '').length}字 思考=${(o.thinking || '').length}字${o.error ? ' err=' + o.error : ''}`);
      return;
    }

    if (type === 'attach') {
      const p = reqId ? this.pending.get(reqId) : null;
      if (p) { clearTimeout(p.timer); this.pending.delete(reqId); p.resolve(o); }
      this.log(`附件挂载 ${o.ok ? '成功' : '失败'} ${o.name || ''}${o.ok ? '' : ' err=' + o.error}`);
      return;
    }

    if (type === 'newChat') {
      const p = reqId ? this.pending.get(reqId) : null;
      if (p) { clearTimeout(p.timer); this.pending.delete(reqId); p.resolve(o); }
      this.log(`新建对话: ${o.ok ? '成功' : '失败'}`);
      return;
    }

    if (type === 'pageReply') {
      this.log(`页面手动回复 正文=${(o.content || '').length}字${o.recalled ? ' [撤回已拦截]' : ''}`);
      return;
    }

    if (type === 'boot' || type === 'probe') {
      if (this.onStatus) this.onStatus(o);
      return;
    }
  }

  failAll(msg) {
    for (const [, p] of this.pending) { clearTimeout(p.timer); p.resolve({ ok: false, error: msg }); }
    this.pending.clear();
    for (const [, s] of this.streams) { clearTimeout(s.timer); if (s.onFinish) s.onFinish({ ok: false, error: msg }); }
    this.streams.clear();
  }
}

module.exports = BridgeController;
