/* ============================================================
 * E2E 登录自动化脚本（仅测试用，随 exe 内嵌，正常运行时不会执行）
 * 由 E2ETest.cs 在 UI 线程反复调用；每次调用重新探测页面并推进一步。
 * 凭据经 window.__E2E_CRED__ 注入，绝不出现在返回值/日志里。
 * ============================================================ */
(function () {
  var C = window.__E2E_CRED__ || {};
  var phone = C.phone || '';
  var password = C.password || '';

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
  function clickByText(kws) {
    var cand = document.querySelectorAll('button,a,div[role=button],span,label');
    for (var i = 0; i < cand.length; i++) {
      var el = cand[i];
      if (!vis(el)) continue;
      var t = (el.innerText || el.textContent || '').replace(/\s+/g, '');
      if (!t || t.length > 12) continue;
      for (var k = 0; k < kws.length; k++) {
        if (t.indexOf(kws[k]) >= 0) { try { el.click(); } catch (e) {} return t; }
      }
    }
    return '';
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

  /* --- 诊断信息（帮助远程定位页面结构变化） --- */
  var ins = all('input');
  for (var i = 0; i < ins.length && i < 12; i++) {
    out.inputs.push({
      type: ins[i].type, name: ins[i].name, ph: ins[i].placeholder || '',
      aria: ins[i].getAttribute('aria-label') || '', maxlength: ins[i].maxLength
    });
  }
  var btns = all('button,a,div[role=button]');
  for (var j = 0; j < btns.length && out.buttonTexts.length < 24; j++) {
    var bt = (btns[j].innerText || '').replace(/\s+/g, ' ').trim();
    if (bt && bt.length <= 16 && out.buttonTexts.indexOf(bt) < 0) out.buttonTexts.push(bt);
  }

  /* --- 验证码/滑块检测：出现即停止自动操作，交回人工 --- */
  var page = (document.body ? document.body.innerText : '') || '';
  if (/拖动滑块|滑动验证|完成拼图|安全验证|请完成验证|图形验证/.test(page) ||
      document.querySelector('.captcha, iframe[src*="captcha"], [class*="slider"]')) {
    out.captcha = true;
    out.stage = 'captcha';
    return JSON.stringify(out);
  }

  /* --- 定位输入框 --- */
  function findPhone() {
    var list = all('input');
    for (var i = 0; i < list.length; i++) {
      var el = list[i], ty = (el.type || 'text').toLowerCase();
      var hint = ((el.placeholder || '') + ' ' + (el.getAttribute('aria-label') || '') + ' ' + (el.name || '')).toLowerCase();
      if ((ty === 'tel' || ty === 'text' || ty === 'email' || ty === 'number') &&
          (hint.indexOf('手机') >= 0 || hint.indexOf('邮箱') >= 0 || hint.indexOf('账号') >= 0 ||
           hint.indexOf('phone') >= 0 || hint.indexOf('email') >= 0 || list.length <= 2)) return el;
    }
    return null;
  }
  function findPwd() {
    var list = all('input');
    for (var i = 0; i < list.length; i++) if ((list[i].type || '').toLowerCase() === 'password') return list[i];
    return null;
  }
  function findSubmit() {
    var cand = all('button,div[role=button],input[type=submit]');
    for (var i = 0; i < cand.length; i++) {
      var t = (cand[i].innerText || cand[i].value || '').replace(/\s+/g, '');
      if (t === '登录' || t === '登入' || t === 'Sign in' || t === 'SignIn') return cand[i];
    }
    return null;
  }
  function agreeCheckbox() {
    var list = all('input[type=checkbox]');
    for (var i = 0; i < list.length; i++) if (!list[i].checked) return list[i];
    return null;
  }

  var pwd = findPwd();
  var phoneEl = findPhone();

  /* --- 1) 尚未展开登录表单：先点开入口 / 切到密码登录 --- */
  if (!pwd) {
    var a1 = clickByText(['密码登录', '使用密码', '账号密码登录', '账号密码']);
    if (a1) out.actions.push('switch:' + a1);
  }
  if (!pwd && !phoneEl) {
    var a2 = clickByText(['登录', '登入', 'Signin', 'SignIn']);
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

  var chk = agreeCheckbox();
  if (chk) { try { chk.click(); out.actions.push('agree'); } catch (e) {} }

  /* --- 3) 提交 --- */
  if (pwd && (pwd.value || '').length > 0) {
    var sb = findSubmit();
    if (sb) { try { sb.click(); out.actions.push('submit'); } catch (e) {} }
    else {
      var a3 = clickByText(['登录', '登入', 'Signin', 'SignIn']);
      if (a3) out.actions.push('submit-text:' + a3);
    }
  }

  out.stage = (!pwd && !phoneEl) ? 'no-form' : (out.actions.length ? 'acting' : 'idle');
  return JSON.stringify(out);
})();
