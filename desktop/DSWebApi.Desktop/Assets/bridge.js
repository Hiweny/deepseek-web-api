/* ============================================================
 * DeepSeek Web API · 注入桥接脚本
 * 运行于 chat.deepseek.com 页面上下文（WebView 注入）
 *
 * 设计原则：不直接调用底层 API、不自行创建会话，一切通过网页层完成：
 *   找输入框 ->（附件先注入 <input type=file>）-> 填入 prompt -> 点击发送按钮。
 *   消息回复通过钩住页面自身的 XHR SSE 流解析（含思考/正文分离与防撤回）。
 *
 * 对外（原生侧）入口 window.DSKB：
 *   send({reqId,prompt})          填入并发送
 *   attachFile({reqId,name,mime,b64})  注入附件（交给官网上传/解析）
 *   newChat({reqId})              新建对话
 *   configure(opts)               设置 thinking/search 策略
 *   setTheme(dark)                切换官网主题
 *   probe()/ping()/inputText()    状态探测
 *
 * 回传 window.DSB.onEvent(json)：{type:'boot'|'probe'|'delta'|'reply'|'attach'|'newChat'|'pageReply'|...}
 * ============================================================ */
(function () {
  if (window.__DSWB_LOADED) {
    try { window.DSB.onEvent(JSON.stringify({ type: 'boot', already: true, url: location.href })); } catch (e) {}
    return;
  }
  window.__DSWB_LOADED = true;

  var CONTENT_FILTER = 'CONTENT_FILTER';
  var RECALL_TIP = '⚠️ 此回复已被撤回，以下为本地缓存内容';
  var RECALL_NOT_FOUND = '⛔ 此回复已被撤回，本地缓存中未找到';

  // 原生侧可配置：thinking/search 策略（auto|on|off）
  var opts = { thinking: 'auto', search: 'auto' };

  // DOM 兜底下的等待关联：sessionId -> reqId（__next 为通配）
  var domWait = {};

  // 当前官网会话 id（来自 URL /a/chat/s/<id>）
  function currentSessionId() {
    try {
      var m = location.pathname.match(/\/a\/chat\/s\/([^\/?#]+)/);
      return m ? m[1] : '';
    } catch (e) { return ''; }
  }

  /* ================= 工具函数 ================= */
  function log(m) { try { console.log('[DSWB] ' + m); } catch (e) {} }

  /* 限流看门狗：同一 reqId 只在等待期间存在一个 */
  var rlWatchers = {};

  function emit(obj) {
    obj.t = Date.now();
    if (obj && obj.type === 'reply' && obj.reqId && rlWatchers[obj.reqId]) {
      try { clearInterval(rlWatchers[obj.reqId]); } catch (e) {}
      delete rlWatchers[obj.reqId];
    }
    try { window.DSB.onEvent(JSON.stringify(obj)); } catch (e) { log('emit fail: ' + e.message); }
  }

  /* 检测页面是否弹出「消息发送频繁 / 稍后重试」一类提示（中英文都覆盖） */
  var RL_RE = /(频繁|太快|过于频繁|稍后(再|重)试|发送太频繁|too many requests|too frequent|rate limit|slow down|try again later)/i;
  function detectRateLimitText() {
    try {
      var sel = '.ds-toast__content, .ds-toast, [class*="toast"], [role="alert"], [class*="alert"], [class*="notify"], [class*="message"]';
      var nodes = document.querySelectorAll(sel);
      for (var i = 0; i < nodes.length && i < 40; i++) {
        var el = nodes[i];
        var st = window.getComputedStyle ? getComputedStyle(el) : null;
        if (st && (st.display === 'none' || st.visibility === 'hidden')) continue;
        var t = (el.textContent || '').trim();
        if (t && t.length <= 80 && RL_RE.test(t)) return t.replace(/\s+/g, ' ');
      }
    } catch (e) {}
    return '';
  }

  /* 检测「对话/上下文达到长度上限，请开启新对话」一类提示（与限流区分开）
     注意：只用 toast/alert 类容器，避免把聊天正文里的普通词误判 */
  var CTX_RE = /((对话|上下文|本条|当前)[^，。,.]{0,10}(长度|上限|已满|过长|超出|达到|超过))|((达到|超过|超出)[^，。,.]{0,6}(最大长度|长度上限|上限))|(请[^，。,.]{0,8}(开启|新建|创建)[^，。,.]{0,4}(新)?对话)|(maximum context length)|(context length exceeded)|(start a new chat)|(too long)/i;
  function detectContextLimitText() {
    try {
      var sel = '.ds-toast__content, .ds-toast, [role="alert"], [class*="toast"], [class*="alert"], [class*="notify"]';
      var nodes = document.querySelectorAll(sel);
      for (var i = 0; i < nodes.length && i < 40; i++) {
        var el = nodes[i];
        var st = window.getComputedStyle ? getComputedStyle(el) : null;
        if (st && (st.display === 'none' || st.visibility === 'hidden')) continue;
        var t = (el.textContent || '').trim();
        if (t && t.length <= 60 && CTX_RE.test(t)) return t.replace(/\s+/g, ' ');
      }
    } catch (e) {}
    return '';
  }

  /* ================= 防撤回：本地原始片段缓存 ================= */
  function rawKey(sid, mid) { return 'dswb_recall_' + (sid || '') + '_' + (mid || ''); }
  function saveRaw(sid, mid, frags) {
    try { if (mid && frags) localStorage.setItem(rawKey(sid, mid), JSON.stringify(frags)); } catch (e) {}
  }
  function loadRaw(sid, mid) {
    try {
      var s = localStorage.getItem(rawKey(sid, mid));
      if (s) return JSON.parse(s);
    } catch (e) {}
    return null;
  }

  /* ================= 附件上传网络监测 ================= */
  var uploads = { active: 0, lastOkAt: 0, lastFail: '', parsedAt: 0, failAt: 0 };
  function isFileUploadUrl(u) { return u && u.indexOf('/api/v0/file/upload_file') !== -1; }
  function isFileFetchUrl(u) { return u && u.indexOf('/api/v0/file/fetch_files') !== -1; }
  function isAnyFileUrl(u) { return u && u.indexOf('/api/v0/file/') !== -1; }

  function noteFileXhr(xhr, url) {
    var up = isFileUploadUrl(url), ff = isFileFetchUrl(url);
    if (!up && !ff) return;
    if (up) uploads.active++;
    xhr.addEventListener('load', function () {
      try {
        if (up) {
          uploads.active = Math.max(0, uploads.active - 1);
          if (xhr.status >= 200 && xhr.status < 300) uploads.lastOkAt = Date.now();
          else { uploads.failAt = Date.now(); uploads.lastFail = 'HTTP ' + xhr.status; }
        }
        if (ff && xhr.status >= 200 && xhr.status < 300) {
          var t = xhr.responseText || '';
          if (t.indexOf('SUCCESS') !== -1) uploads.parsedAt = Date.now();
        }
      } catch (e) {}
    });
    xhr.addEventListener('error', function () {
      if (up) { uploads.active = Math.max(0, uploads.active - 1); uploads.failAt = Date.now(); uploads.lastFail = 'network'; }
    });
  }

  if (window.fetch) {
    var _origFetch = window.fetch.bind(window);
    window.fetch = function (input, init) {
      var url = typeof input === 'string' ? input : ((input && input.url) || '');
      var up = isFileUploadUrl(url), ff = isFileFetchUrl(url);
      if (up) uploads.active++;
      var p = _origFetch(input, init);
      if (up || ff) {
        p.then(function (resp) {
          if (up) {
            uploads.active = Math.max(0, uploads.active - 1);
            if (resp.ok) uploads.lastOkAt = Date.now();
            else { uploads.failAt = Date.now(); uploads.lastFail = 'HTTP ' + resp.status; }
          }
          if (ff && resp.ok) {
            try {
              resp.clone().json().then(function (j) {
                if (JSON.stringify(j).indexOf('SUCCESS') !== -1) uploads.parsedAt = Date.now();
              }).catch(function () {});
            } catch (e) {}
          }
          return resp;
        }).catch(function (e) {
          if (up) { uploads.active = Math.max(0, uploads.active - 1); uploads.failAt = Date.now(); uploads.lastFail = '' + e; }
        });
      }
      return p;
    };
  }

  /* ================= SSE op 树状态机（思考/正文分离 + 防撤回） ================= */
  function _parseKey(key, container) {
    if (Array.isArray(container) && /^[-+]?\d+$/.test(key)) {
      var i = parseInt(key, 10);
      return i < 0 ? container.length + i : i;
    }
    return key;
  }
  function _setValueByPath(obj, path, value, isAppend) {
    var keys = path.split('/'), current = obj;
    for (var i = 0; i < keys.length - 1; i++) {
      var key = _parseKey(keys[i], current);
      if (!(key in current)) current[key] = typeof _parseKey(keys[i + 1], current) === 'number' ? [] : {};
      current = current[key];
    }
    var lastKey = _parseKey(keys[keys.length - 1], current);
    if (isAppend) {
      if (Array.isArray(current[lastKey])) current[lastKey] = current[lastKey].concat(value);
      else current[lastKey] = (current[lastKey] || '') + value;
    } else {
      current[lastKey] = value;
    }
    return obj;
  }

  function DSState() {
    this.fields = {};
    this.sessId = '';
    this.recalled = false;
    this.recalledContent = '';
    this.recalledRaw = null;
    this._updatePath = '';
    this._updateMode = 'SET';
  }
  DSState.prototype.preCheck = function (data) {
    // 在 setField 之前执行：此刻 fragments 仍是真实内容（防撤回关键时机）
    var path = data.p !== undefined ? data.p : this._updatePath;
    var mode = data.o !== undefined ? data.o : this._updateMode;
    if (mode === 'BATCH' && path === 'response' && Array.isArray(data.v)) {
      for (var i = 0; i < data.v.length; i++) {
        var v = data.v[i];
        if (v && v.p === 'fragments' && v.v && v.v.length > 0 && v.v[0] && v.v[0].type === 'TEMPLATE_RESPONSE') {
          var frags = this.rawFragments();
          this.recalledRaw = frags;
          this.recalledContent = extractByType(frags, 'RESPONSE');
          this.recalledThinking = extractByType(frags, 'THINK');
          this.recalled = true;
          try { saveRaw(this.sessId, this.messageId(), frags); } catch (e) {}
        }
        if (v && v.p === 'status' && v.v === CONTENT_FILTER) {
          this.recalled = true;
        }
      }
    }
  };
  DSState.prototype.update = function (data) {
    this.preCheck(data);
    if (data.p !== undefined) this._updatePath = data.p;
    if (data.o !== undefined) this._updateMode = data.o;
    var value = data.v;
    if (typeof value === 'object' && value !== null && this._updatePath === '') {
      for (var key in value) { if (value.hasOwnProperty(key)) this.fields[key] = value[key]; }
      return;
    }
    this.setField(this._updatePath, value, this._updateMode);
  };
  DSState.prototype.setField = function (path, value, mode) {
    if (mode === 'BATCH') {
      for (var i = 0; i < value.length; i++) {
        var v = value[i];
        this.setField(path + '/' + v.p, v.v, v.o || 'SET');
      }
    } else if (mode === 'SET') {
      _setValueByPath(this.fields, path, value, false);
    } else if (mode === 'APPEND') {
      _setValueByPath(this.fields, path, value, true);
    }
  };
  DSState.prototype.rawFragments = function () {
    try { return (this.fields.response && this.fields.response.fragments) || []; } catch (e) { return []; }
  };
  DSState.prototype.messageId = function () {
    try { return (this.fields.response && this.fields.response.message_id) || ''; } catch (e) { return ''; }
  };
  function extractByType(frags, type) {
    var list = frags || [];
    var out = '';
    for (var i = 0; i < list.length; i++) {
      var f = list[i];
      if (!f || !f.content) continue;
      // DeepSeek 官方片段类型：THINK（思考）/ RESPONSE（正文）
      if (type === 'THINK' && (f.type === 'THINK' || f.type === 'THINKING')) out += f.content;
      else if (type === 'RESPONSE' && f.type === 'RESPONSE') out += f.content;
    }
    return out;
  }
  DSState.prototype.thinking = function () {
    if (this.recalled && this.recalledThinking) return this.recalledThinking;
    return extractByType(this.rawFragments(), 'THINK');
  };
  DSState.prototype.content = function () {
    if (this.recalled && this.recalledContent) return this.recalledContent;
    return extractByType(this.rawFragments(), 'RESPONSE');
  };

  function feedSSE(state, rawText, lastLen) {
    if (!rawText || rawText.length <= lastLen) return lastLen;
    var newPart = rawText.substring(lastLen);
    var lines = newPart.split('\n');
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      if (!line || line.indexOf('data:') !== 0) continue;
      try {
        var data = JSON.parse(line.replace(/^data:\s*/, ''));
        if (data && data.v !== undefined) state.update(data);
      } catch (e) {}
    }
    return rawText.length;
  }

  /* ================= XHR 钩子：回复流 + 附件 + 请求改写 ================= */
  function isCompletionUrl(url) {
    return url.indexOf('/api/v0/chat/completion') !== -1 ||
           url.indexOf('/api/v0/chat/edit_message') !== -1 ||
           url.indexOf('/api/v0/chat/regenerate') !== -1 ||
           url.indexOf('/api/v0/chat/continue') !== -1 ||
           url.indexOf('/api/v0/chat/resume_stream') !== -1;
  }
  function isHistoryUrl(url) {
    return url.indexOf('/api/v0/chat_session/fetch_page') !== -1 ||
           url.indexOf('/api/v0/chat/history_messages') !== -1;
  }

  var _origOpen = XMLHttpRequest.prototype.open;
  var _origSend = XMLHttpRequest.prototype.send;
  var _respTextDesc = Object.getOwnPropertyDescriptor(XMLHttpRequest.prototype, 'responseText');
  var _origRespTextGetter = _respTextDesc ? _respTextDesc.get : null;

  XMLHttpRequest.prototype.open = function (method, url) {
    this._dswb_url = (url || '') + '';
    return _origOpen.apply(this, arguments);
  };

  XMLHttpRequest.prototype.send = function (body) {
    var xhr = this;
    var url = xhr._dswb_url || '';
    if (isAnyFileUrl(url)) { try { noteFileXhr(xhr, url); } catch (e) {} }

    var effBody = body;
    if (isCompletionUrl(url) && typeof body === 'string') {
      try {
        var j = JSON.parse(body);
        // 思考：默认开（'auto' 也当开），只有显式 'off' 才关。
        j.thinking_enabled = (opts.thinking !== 'off');
        // 联网搜索：默认关 —— 网页自带的联网搜索会让模型把自己的搜索结果
        // 与外部工具结果混淆；只有显式 'on' 才开。
        j.search_enabled = (opts.search === 'on');
        effBody = JSON.stringify(j);
        log('inject body: thinking_enabled=' + j.thinking_enabled + ' search_enabled=' + j.search_enabled);
      } catch (e) {}
      try { hookPageCompletion(xhr, effBody); } catch (e) { log('hook fail: ' + e.message); }
    } else if (isHistoryUrl(url)) {
      try { hookHistory(xhr); } catch (e) {}
    }
    return _origSend.call(this, effBody);
  };

  function hookPageCompletion(xhr, body) {
    var sessId = '';
    try { if (body) sessId = JSON.parse(body).chat_session_id || ''; } catch (e) {}
    var state = new DSState();
    state.sessId = sessId;
    var lastLen = 0;
    var recallPatched = false, patchedText = null;
    var lastT = '', lastC = '';
    var finished = false;

    function currentReqId() { return (sessId && domWait[sessId]) || domWait.__next || null; }
    function releaseReqId(rid) {
      if (!rid) return;
      if (sessId && domWait[sessId] === rid) delete domWait[sessId];
      else if (domWait.__next === rid) delete domWait.__next;
    }
    function flushDelta() {
      if (finished) return;
      var rid = currentReqId();
      if (!rid) return;
      var th = state.thinking(), ct = state.content();
      if (th === lastT && ct === lastC) return;
      lastT = th; lastC = ct;
      emit({ type: 'delta', reqId: rid, thinking: th, content: ct, recalled: state.recalled });
    }

    if (_origRespTextGetter) {
      try {
        Object.defineProperty(xhr, 'responseText', {
          get: function () {
            var raw;
            try { raw = _origRespTextGetter.call(xhr); } catch (e) { return null; }
            if (!raw) return raw;
            lastLen = feedSSE(state, raw, lastLen);
            // 页面内可视防撤回：用缓存内容替换被撤回的 SSE 片段
            if (state.recalled && !recallPatched) {
              recallPatched = true;
              try {
                var m = raw.match(/data:\s*(\{[^\n]*"fragments"[^\n]*TEMPLATE_RESPONSE[^\n]*\})/);
                if (m) {
                  patchedText = raw.replace(m[1], JSON.stringify({
                    v: [{ v: [{ id: 1, type: 'TIP', style: 'WARNING', content: RECALL_TIP }], p: 'fragments', o: 'APPEND' }],
                    p: 'response', o: 'BATCH'
                  }));
                }
              } catch (e) {}
              if (!patchedText) patchedText = raw;
            }
            flushDelta();
            return (state.recalled && patchedText) ? patchedText : raw;
          },
          configurable: true,
          enumerable: true
        });
      } catch (e) {}
    }

    xhr.addEventListener('progress', function () { try { void xhr.responseText; } catch (e) {} });
    var poll = setInterval(function () {
      try { if (xhr.readyState >= 3) void xhr.responseText; } catch (e) {}
    }, 300);

    function finish(errMsg) {
      if (finished) return;
      finished = true;
      clearInterval(poll);
      try { lastLen = feedSSE(state, _origRespTextGetter.call(xhr) || '', lastLen); } catch (e) {}
      var content = state.content(), thinking = state.thinking();
      var rid = currentReqId();
      if (rid) {
        releaseReqId(rid);
        emit({
          type: 'reply', reqId: rid, ok: !!(content || thinking), via: 'dom',
          content: content, thinking: thinking, recalled: state.recalled,
          messageId: state.messageId(), sessionId: sessId,
          error: errMsg || ((content || thinking) ? '' : 'EMPTY')
        });
      } else {
        emit({ type: 'pageReply', sessionId: sessId, content: content, thinking: thinking, recalled: state.recalled });
      }
    }

    xhr.addEventListener('load', function () { finish(''); });
    xhr.addEventListener('error', function () { finish('NETWORK'); });
    xhr.addEventListener('abort', function () { finish('ABORT'); });
    xhr.addEventListener('timeout', function () { finish('TIMEOUT'); });
  }

  // 历史回放防撤回：status=CONTENT_FILTER 的消息用本地缓存替换
  function hookHistory(xhr) {
    if (!_origRespTextGetter) return;
    try {
      Object.defineProperty(xhr, 'responseText', {
        get: function () {
          var raw = _origRespTextGetter.call(xhr);
          if (!raw) return raw;
          try {
            var json = JSON.parse(raw);
            var data = json && json.data && json.data.biz_data;
            if (!data || !data.chat_messages) return raw;
            var sid = (data.chat_session && data.chat_session.id) || '';
            var modified = false;
            for (var i = 0; i < data.chat_messages.length; i++) {
              var msg = data.chat_messages[i];
              if (msg.status === CONTENT_FILTER) {
                var cached = loadRaw(sid, msg.message_id);
                msg.fragments = cached || [{ content: RECALL_NOT_FOUND, id: 2, type: 'TEMPLATE_RESPONSE' }];
                msg.status = 'FINISHED';
                modified = true;
              }
            }
            if (modified) return JSON.stringify(json);
          } catch (e) {}
          return raw;
        },
        configurable: true,
        enumerable: true
      });
    } catch (e) {}
  }

  /* ================= DOM：输入框 / 发送按钮 ================= */
  function findInput() {
    return document.querySelector('textarea#chat-input') ||
           document.querySelector('textarea[placeholder]') ||
           document.querySelector('textarea') ||
           document.querySelector('[contenteditable="true"]') ||
           document.querySelector('[role="textbox"]');
  }
  function nativeFill(el, text) {
    if (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') {
      var proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, text);
      el.dispatchEvent(new Event('input', { bubbles: true }));
    } else {
      el.textContent = text;
      el.dispatchEvent(new InputEvent('input', { bubbles: true, data: text, inputType: 'insertText' }));
    }
  }
  function inputScopeButtons(inputEl) {
    var scope = inputEl;
    for (var i = 0; i < 10 && scope; i++) {
      scope = scope.parentElement;
      if (!scope || scope === document.body) break;
      var bs = scope.querySelectorAll('button, .ds-button, [role="button"]');
      if (bs.length >= 2) return Array.prototype.slice.call(bs);
    }
    return inputEl ? Array.prototype.slice.call(document.querySelectorAll('button, [role="button"]')) : [];
  }
  function isBtnDisabled(b) {
    var cls = (b.className || '') + '';
    return b.disabled === true || b.getAttribute('aria-disabled') === 'true' || cls.indexOf('disabled') !== -1;
  }
  function findSendButton(inputEl) {
    var btns = inputScopeButtons(inputEl);
    for (var i = 0; i < btns.length; i++) {
      var cls = (btns[i].className || '') + '';
      if (cls.indexOf('ds-button--primary') !== -1 && (cls.indexOf('filled') !== -1 || cls.indexOf('circle') !== -1)) return btns[i];
    }
    for (var j = 0; j < btns.length; j++) {
      var al = (btns[j].getAttribute('aria-label') || '').toLowerCase();
      if (al.indexOf('send') !== -1 || al.indexOf('发送') !== -1) return btns[j];
    }
    for (var k = 0; k < btns.length; k++) {
      var txt = (btns[k].textContent || '').trim();
      if (txt === '发送' || txt === 'Send') return btns[k];
    }
    return btns.length ? btns[btns.length - 1] : null;
  }

  /* ================= 附件：文件输入框注入 ================= */
  function b64ToBytes(b64) {
    var bin = atob(b64);
    var len = bin.length, u8 = new Uint8Array(len);
    for (var i = 0; i < len; i++) u8[i] = bin.charCodeAt(i);
    return u8;
  }
  function listFileInputs() { return Array.prototype.slice.call(document.querySelectorAll('input[type="file"]')); }
  function pickFileInput(mime) {
    var list = listFileInputs().filter(function (i) { return !i.disabled; });
    if (!list.length) return null;
    var isImg = (mime || '').indexOf('image/') === 0;
    for (var i = 0; i < list.length; i++) {
      var acc = (list[i].getAttribute('accept') || '').toLowerCase();
      if (!acc) continue;
      if (isImg && (acc.indexOf('image') !== -1 || acc.indexOf('.png') !== -1 || acc.indexOf('.jpg') !== -1)) return list[i];
      if (!isImg && acc.indexOf('image') === -1) return list[i];
    }
    var permissive = list.filter(function (i) {
      var a = (i.getAttribute('accept') || '').trim();
      return a === '' || a.indexOf('*/*') !== -1;
    });
    if (permissive.length) return permissive[permissive.length - 1];
    return list[list.length - 1];
  }
  function clickAttachToggle() {
    var input = findInput();
    var scope = input ? input.parentElement : document.body;
    var nodes = scope.querySelectorAll('button, [role="button"], div[class*="icon"], span[class*="icon"]');
    for (var i = 0; i < nodes.length; i++) {
      var n = nodes[i];
      var al = (n.getAttribute('aria-label') || '').toLowerCase();
      var cls = (n.className || '').toString().toLowerCase();
      if (/upload|attach|file|add|上传|附件|添加/.test(al) || (/upload|attach|attach-file|add/.test(cls) && !/send/.test(cls))) {
        try { n.click(); return true; } catch (e) {}
      }
    }
    return false;
  }
  function assignFiles(input, file) {
    var dt = new DataTransfer();
    dt.items.add(file);
    try { input.files = dt.files; } catch (e) {
      try { Object.defineProperty(input, 'files', { value: dt.files, configurable: true, writable: true }); }
      catch (e2) { return false; }
    }
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true, cancelable: true }));
    return true;
  }
  function attachmentSettled(name) {
    var input = findInput();
    var scope = input;
    for (var i = 0; i < 6 && scope; i++) { scope = scope.parentElement; if (!scope) break; }
    if (!scope) scope = document.body;
    var base = (name || '').replace(/\.[^.]+$/, '').toLowerCase();
    var chip = false;
    if (base) {
      var txts = scope.querySelectorAll('*');
      for (var j = 0; j < txts.length; j++) {
        var own = txts[j].childNodes;
        for (var k = 0; k < own.length; k++) {
          if (own[k].nodeType === 3 && (own[k].textContent || '').toLowerCase().indexOf(base) !== -1) { chip = true; break; }
        }
        if (chip) break;
      }
    }
    var busy = scope.querySelector('[class*="loading" i],[class*="progress" i],[class*="spinner" i],[role="progressbar"]') ||
               /上传中|解析中|正在处理|uploading|processing/i.test(scope.textContent || '');
    return chip && !busy;
  }
  function awaitAttached(o, file, dispatchAt) {
    var deadline = Date.now() + 120000;
    var netGraceAt = 0;
    (function check() {
      var now = Date.now();
      var netOk = uploads.lastOkAt >= dispatchAt;
      var parsed = uploads.parsedAt >= dispatchAt;
      var failedAfter = uploads.failAt >= dispatchAt;
      var chip = attachmentSettled(file.name);
      if (netOk && netGraceAt === 0) netGraceAt = now;

      if ((netOk && (parsed || chip || (netGraceAt && now - netGraceAt > 4000))) ||
          (!netOk && chip && uploads.active === 0 && now - dispatchAt > 3000)) {
        emit({ type: 'attach', reqId: o.reqId, ok: true, name: file.name, via: netOk ? 'net' : 'dom' });
        return;
      }
      if (failedAfter && !netOk) {
        emit({ type: 'attach', reqId: o.reqId, ok: false, name: file.name, error: 'UPLOAD_FAIL:' + uploads.lastFail });
        return;
      }
      if (now > deadline) {
        if (netOk || chip) emit({ type: 'attach', reqId: o.reqId, ok: true, name: file.name, via: 'timeout-pass' });
        else emit({ type: 'attach', reqId: o.reqId, ok: false, name: file.name, error: 'ATTACH_TIMEOUT' });
        return;
      }
      setTimeout(check, 400);
    })();
  }
  function domAttachFile(o) {
    try {
      var bytes = b64ToBytes(o.b64 || '');
      if (!bytes.length) { emit({ type: 'attach', reqId: o.reqId, ok: false, name: o.name, error: 'EMPTY' }); return; }
      var file = new File([bytes], o.name || 'file', { type: o.mime || 'application/octet-stream' });
      var tries = 0;
      (function ensureAndAssign() {
        var input = pickFileInput(o.mime);
        if (!input) {
          if (tries++ < 6) { clickAttachToggle(); setTimeout(ensureAndAssign, 350); return; }
          emit({ type: 'attach', reqId: o.reqId, ok: false, name: file.name, error: 'NO_FILE_INPUT' });
          return;
        }
        var dispatchAt = Date.now();
        if (!assignFiles(input, file)) {
          emit({ type: 'attach', reqId: o.reqId, ok: false, name: file.name, error: 'ASSIGN_FAIL' });
          return;
        }
        awaitAttached(o, file, dispatchAt);
      })();
    } catch (e) {
      emit({ type: 'attach', reqId: o.reqId, ok: false, name: o.name, error: 'EX:' + e.message });
    }
  }

  /* ================= 主流程：填入 + 发送 ================= */
  function domSend(o) {
    try { applyToggles(); } catch (e) {}
    var input = findInput();
    if (!input) { emit({ type: 'reply', reqId: o.reqId, ok: false, via: 'dom', content: '', error: 'NO_INPUT', sessionId: '' }); return; }
    if (o.sessionId) domWait[o.sessionId] = o.reqId;
    domWait.__next = o.reqId;

    nativeFill(input, o.prompt);

    var startedAt = Date.now();
    function enterFallback() {
      try {
        input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }));
      } catch (e) {}
    }
    function attempt() {
      var btn = findSendButton(input);
      if (btn) {
        if (!isBtnDisabled(btn)) {
          try { btn.click(); } catch (e) { log('click fail: ' + e.message); }
          return;
        }
        if (Date.now() - startedAt > 90000) { log('发送按钮持续不可用，改用 Enter'); enterFallback(); return; }
        setTimeout(attempt, 150);
      } else {
        enterFallback();
      }
    }
    setTimeout(attempt, 60);

    // 限流看门狗：点发送后 2 秒起持续观察页面提示；命中即上报并结束本 reqId
    (function watchRateLimit() {
      var started = Date.now();
      rlWatchers[o.reqId] = setInterval(function () {
        var txt = detectRateLimitText();
        var ctx = txt ? '' : detectContextLimitText();
        if (ctx) {
          try { clearInterval(rlWatchers[o.reqId]); } catch (e) {}
          delete rlWatchers[o.reqId];
          var ridc = (o.sessionId && domWait[o.sessionId]) || domWait.__next;
          if (ridc === o.reqId) {
            if (o.sessionId && domWait[o.sessionId] === ridc) delete domWait[o.sessionId];
            else if (domWait.__next === ridc) delete domWait.__next;
          }
          emit({ type: 'contextLimit', reqId: o.reqId, via: 'dom', message: ctx, sessionId: o.sessionId || '' });
          emit({ type: 'reply', reqId: o.reqId, ok: false, via: 'dom', content: '', thinking: '',
                 error: 'CONTEXT_LIMIT: ' + ctx, sessionId: o.sessionId || '', recalled: false });
          return;
        }
        if (txt) {
          try { clearInterval(rlWatchers[o.reqId]); } catch (e) {}
          delete rlWatchers[o.reqId];
          var rid = (o.sessionId && domWait[o.sessionId]) || domWait.__next;
          if (rid === o.reqId) {
            if (o.sessionId && domWait[o.sessionId] === rid) delete domWait[o.sessionId];
            else if (domWait.__next === rid) delete domWait.__next;
          }
          emit({ type: 'rateLimited', reqId: o.reqId, via: 'dom', message: txt, sessionId: o.sessionId || '' });
          emit({ type: 'reply', reqId: o.reqId, ok: false, via: 'dom', content: '', thinking: '',
                 error: 'RATE_LIMITED: ' + txt, sessionId: o.sessionId || '', recalled: false });
          return;
        }
        if (Date.now() - started > 300000) { try { clearInterval(rlWatchers[o.reqId]); } catch (e) {} delete rlWatchers[o.reqId]; }
      }, 700);
    })();

    setTimeout(function () {
      var rid = (o.sessionId && domWait[o.sessionId]) || domWait.__next;
      if (rid === o.reqId) {
        if (o.sessionId && domWait[o.sessionId] === rid) delete domWait[o.sessionId];
        else if (domWait.__next === rid) delete domWait.__next;
        emit({ type: 'reply', reqId: o.reqId, ok: false, via: 'dom', content: '', error: 'DOM_TIMEOUT', sessionId: o.sessionId || '' });
      }
    }, 240000);
  }

  function clickNewChat() {
    var target = null;
    var candidates = document.querySelectorAll('div, a, button, span');
    for (var i = 0; i < candidates.length; i++) {
      var el = candidates[i];
      if (el.children.length > 3) continue;
      var txt = (el.textContent || '').trim();
      if (txt === 'New chat' || txt === '开启新对话' || txt === '新对话' || txt === '新建对话') {
        target = el;
        if (el.tagName === 'A' || el.getAttribute('role') === 'button' || (el.className || '').indexOf('cursor') !== -1) break;
      }
    }
    if (!target) {
      var bs = document.querySelectorAll('button, a, [role="button"]');
      for (var j = 0; j < bs.length; j++) {
        var sig = ((bs[j].getAttribute('aria-label') || '') + ' ' + (bs[j].getAttribute('title') || '')).toLowerCase();
        if (sig.indexOf('new chat') !== -1 || sig.indexOf('新对话') !== -1 || sig.indexOf('新建对话') !== -1) { target = bs[j]; break; }
      }
    }
    if (target) { try { target.click(); return true; } catch (e) {} }
    return false;
  }

  /* ================= 主题 ================= */
  function applyTheme(dark) {
    try {
      localStorage.setItem('__appKit_@deepseek/chat_themePreference',
        JSON.stringify({ value: dark ? 'dark' : 'light', __version: '0' }));
    } catch (e) {}
    try {
      var b = document.body;
      if (b) {
        b.classList.toggle('dark', !!dark);
        b.classList.toggle('light', !dark);
        b.setAttribute('data-theme', dark ? 'dark' : 'light');
      }
    } catch (e) {}
  }

  /* ================= 会话变化监听（供原生侧重置上下文计数） ================= */
  var _lastSid = currentSessionId();
  function watchSession() {
    var sid = currentSessionId();
    if (sid !== _lastSid) {
      _lastSid = sid;
      emit({ type: 'session', sessionId: sid });
    }
    setTimeout(watchSession, 1000);
  }

  /* ============ 官网「深度思考 / 智能搜索」按钮同步 ============ */
  // 页面用 .ds-toggle-button + aria-pressed 表示开关（实测 selector）。
  // 真正生效靠注入请求体里的 thinking_enabled / search_enabled；这里同步按钮
  // 是为了让界面显示与实际请求一致，并作为兜底。
  function matchToggle(texts) {
    try {
      var nodes = document.querySelectorAll('.ds-toggle-button, [class*="toggle-button"], [class*="toggle"], [role="switch"], button[aria-pressed]');
      for (var i = 0; i < nodes.length; i++) {
        var el = nodes[i];
        var t = (el.textContent || '').replace(/\s+/g, '');
        if (t.length > 16) continue;
        for (var k = 0; k < texts.length; k++) {
          if (t.indexOf(texts[k]) >= 0) return el;
        }
      }
    } catch (e) {}
    return null;
  }
  function toggleIsOn(el) {
    if (!el) return null;
    var a = el.getAttribute('aria-pressed');
    if (a === null) a = el.getAttribute('aria-checked');
    if (a !== null) return a === 'true';
    return /(^|\s)(active|selected|checked)(\s|$)/.test(el.className || '');
  }
  function syncToggle(texts, wantOn) {
    var el = matchToggle(texts);
    if (!el) return { found: false };
    var on = toggleIsOn(el);
    if (on !== wantOn) { try { el.click(); } catch (e) {} }
    return { found: true, before: on, want: wantOn };
  }
  function applyToggles() {
    // 思考：默认开；联网搜索：默认关（'智能搜索' 是现行文案，'联网搜索' 是旧文案）
    var th = syncToggle(['深度思考', '深度思考(R1)', '智能思考', '深度思考R1'], opts.thinking !== 'off');
    var se = syncToggle(['联网搜索', '智能搜索'], opts.search === 'on');
    return { thinking: th, search: se };
  }

  /* ================= 原生侧入口 ================= */
  window.DSKB = {
    ping: function () { return 'pong'; },

    configure: function (o) {
      o = o || {};
      if (o.thinking) opts.thinking = o.thinking;
      if (o.search) opts.search = o.search;
      var toggles = applyToggles();     // 同步官网「深度思考 / 智能搜索」按钮
      return JSON.stringify({ opts: opts, toggles: toggles });
    },

    setTheme: function (dark) { applyTheme(!!dark); return 'ok'; },

    probe: function () {
      var hasInput = !!findInput();
      var hasFileInput = listFileInputs().length > 0;
      // 只要不在登录页且输入框存在，即视为已登录（不依赖具体 cookie 名）
      var loggedIn = location.href.indexOf('sign_in') === -1 && location.href.indexOf('login') === -1 && hasInput;
      emit({
        type: 'probe', url: location.href, loggedIn: loggedIn,
        hasInput: hasInput, hasFileInput: hasFileInput, sessionId: currentSessionId(),
        ready: loggedIn && hasInput, title: document.title
      });
    },

    send: function (o) { domSend(o || {}); },
    attachFile: function (o) { domAttachFile(o || {}); },
    newChat: function (o) {
      o = o || {};
      var ok = clickNewChat();
      emit({ type: 'newChat', reqId: o.reqId || '', ok: ok, sessionId: currentSessionId() });
    },
    sessionId: function () { return currentSessionId(); },
    inputText: function () { var el = findInput(); return el ? (el.value || el.textContent || '') : ''; }
  };

  emit({ type: 'boot', url: location.href, loggedIn: location.href.indexOf('sign_in') === -1 });
  setTimeout(watchSession, 500);
  log('bridge loaded @ ' + location.href);
})();
