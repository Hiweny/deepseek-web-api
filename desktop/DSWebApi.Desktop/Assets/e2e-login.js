/* ============================================================
 * E2E 登录自动化脚本（仅测试用，随 exe 内嵌，正常运行时不会执行）
 * 由 E2ETest.cs 在 UI 线程反复调用；每次调用重新探测页面并推进一步。
 * 凭据经 window.__E2E_CRED__ 注入，绝不出现在返回值/日志里。
 *
 * 注意：CI 上官网是英文界面（按钮是 "Log in" 而不是「登录」），
 *       且首次访问会弹 Cookie 设置，必须先关掉否则会挡住点击。
 * ============================================================ */
(function () {
  var C = window.__E2E_CRED__ || {};
  var phone = C.phone || '';
  var password = C.password || '';

  var SUBMIT_WORDS = ['login', 'log in', 'signin', 'sign in', 'logon', 'continue',
                      '登录', '登入', '登陆'];
  var COOKIE_WORDS = ['accept all cookies', 'necessary cookies only', 'accept all',
                      'accept cookies', 'accept', 'agree', 'got it', 'ok',
                      '接受全部', '仅必要', '同意', '我知道了'];

  function norm(s) { return (s || '').replace(/\s+/g, '').toLowerCase(); }
  function vis(el) {
    if (!el) return false;
    try {
      var r = el.getBoundingClientRect();
      if (r.width < 2 || r.height < 2) return false;
      var st = getComputedStyle(el);
      if (st.display === 'none' || st.visibility === 'hidden' || st.opacity === '0') return false;
      if (el.disabled) return false;
      return true;
    } catch (e) { return false; }
  }
  function all(sel) {
    var a = document.querySelectorAll(sel), out = [];
    for (var i = 0; i < a.length; i++) if (vis(a[i])) out.push(a[i]);
    return out;
  }
  function textOf(el) { return (el.innerText || el.textContent || el.value || '').replace(/\s+/g, ' ').trim(); }

  /* 找可点击元素：优先精确等于目标词，其次包含目标词 */
  function findByWords(words, sel, maxLen) {
    var cand = all(sel || 'button,a,div[role=button],input[type=submit],span');
    var loose = null;
    for (var i = 0; i < cand.length; i++) {
      var el = cand[i];
      var raw = textOf(el);
      if (!raw || raw.length > (maxLen || 24)) continue;
      var n = norm(raw);
      for (var k = 0; k < words.length; k++) {
        var w = norm(words[k]);
        if (n === w) return { el: el, text: raw, exact: true };
        if (!loose && n.indexOf(w) >= 0 && n.length <= w.length + 6) loose = { el: el, text: raw, exact: false };
      }
    }
    return loose;
  }
  function clickWords(words, sel, maxLen) {
    var hit = findByWords(words, sel, maxLen);
    if (!hit) return '';
    try {
      hit.el.click();
      // 有些自定义按钮只认 pointer/mouse 事件
      ['mousedown', 'mouseup', 'pointerdown', 'pointerup'].forEach(function (t) {
        try { hit.el.dispatchEvent(new MouseEvent(t, { bubbles: true })); } catch (e) {}
      });
      return hit.text;
    } catch (e) { return ''; }
  }
  function setVal(el, val) {
    try {
      var proto = (el.tagName === 'TEXTAREA') ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
      var d = Object.getOwnPropertyDescriptor(proto, 'value');
      el.focus();
      d.set.call(el, val);
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      el.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true }));
      el.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true }));
      return true;
    } catch (e) { return false; }
  }

  var out = { url: location.href, title: document.title, actions: [], captcha: false, inputs: [], buttonTexts: [] };

  /* --- 诊断快照 --- */
  var ins = all('input');
  for (var i = 0; i < ins.length && i < 12; i++) {
    out.inputs.push({
      type: ins[i].type, name: ins[i].name, ph: ins[i].placeholder || '',
      aria: ins[i].getAttribute('aria-label') || ''
    });
  }
  var btns = all('button,a,div[role=button]');
  for (var j = 0; j < btns.length && out.buttonTexts.length < 24; j++) {
    var bt = textOf(btns[j]);
    if (bt && bt.length <= 24 && out.buttonTexts.indexOf(bt) < 0) out.buttonTexts.push(bt);
  }
  try { out.bodyText = (document.body ? document.body.innerText : '').replace(/\s+/g, ' ').slice(0, 400); } catch (e) {}

  /* --- 验证码/滑块：出现即停止自动操作 --- */
  var page = (document.body ? document.body.innerText : '') || '';
  if (/拖动滑块|滑动验证|完成拼图|安全验证|请完成验证|图形验证|verify|verification code|captcha/i.test(page) ||
      document.querySelector('iframe[src*="captcha"], [class*="slider"], [class*="captcha"]')) {
    out.captcha = true;
    out.stage = 'captcha';
    return JSON.stringify(out);
  }

  /* --- 0) 关掉 Cookie 弹窗（CI 上英文 "Accept all cookies"，会挡住按钮） --- */
  if (/cookie/i.test(page)) {
    var ck = clickWords(COOKIE_WORDS, 'button,div[role=button],a,span', 30);
    if (ck) out.actions.push('cookie:' + ck);
  }

  function findPhone() {
    var list = all('input');
    for (var i = 0; i < list.length; i++) {
      var el = list[i], ty = (el.type || 'text').toLowerCase();
      var hint = norm((el.placeholder || '') + ' ' + (el.getAttribute('aria-label') || '') + ' ' + (el.name || ''));
      if ((ty === 'tel' || ty === 'text' || ty === 'email' || ty === 'number') &&
          (hint.indexOf('phone') >= 0 || hint.indexOf('email') >= 0 || hint.indexOf('account') >= 0 ||
           hint.indexOf('手机') >= 0 || hint.indexOf('邮箱') >= 0 || hint.indexOf('账号') >= 0 || list.length <= 2)) return el;
    }
    return null;
  }
  function findPwd() {
    var list = all('input');
    for (var i = 0; i < list.length; i++) if (norm(list[i].type) === 'password') return list[i];
    return null;
  }

  /* --- 1) 展开登录表单 / 切到密码登录 --- */
  var pwd = findPwd(), phoneEl = findPhone();
  if (!pwd) {
    var a1 = clickWords(['密码登录', '账号密码', 'password'], 'button,div[role=button],a,span', 20);
    if (a1) out.actions.push('switch:' + a1);
  }
  if (!pwd && !phoneEl) {
    var a2 = clickWords(['登录', 'login', 'sign in'], 'button,div[role=button],a', 20);
    if (a2) out.actions.push('open:' + a2);
  }

  /* --- 2) 填表 --- */
  pwd = findPwd();
  phoneEl = findPhone();
  if (phoneEl && phone && (phoneEl.value || '') !== phone) {
    if (setVal(phoneEl, phone)) out.actions.push('fill:phone');
  }
  if (pwd && password && (pwd.value || '') !== password) {
    if (setVal(pwd, password)) out.actions.push('fill:pwd');
  }

  /* --- 3) 提交（最多 4 次，避免疯狂重复提交） --- */
  var tries = window.__E2E_SUBMITS__ || 0;
  if (pwd && (pwd.value || '').length > 0 && tries < 4) {
    var sb = clickWords(SUBMIT_WORDS, 'button,input[type=submit],div[role=button]', 22);
    if (!sb) {
      // 兜底：表单里最后一个可点按钮
      var forms = all('form');
      if (forms.length) {
        var bs = forms[0].querySelectorAll('button,input[type=submit]');
        for (var m = bs.length - 1; m >= 0; m--) { if (vis(bs[m])) { try { bs[m].click(); sb = textOf(bs[m]) + '(form)'; break; } catch (e) {} } }
      }
    }
    if (sb) { window.__E2E_SUBMITS__ = tries + 1; out.actions.push('submit:' + sb); }
    else out.actions.push('submit-not-found');
  }

  out.submits = window.__E2E_SUBMITS__ || 0;
  out.stage = (!pwd && !phoneEl) ? 'no-form' : (out.actions.length ? 'acting' : 'idle');
  return JSON.stringify(out);
})();
