"""临时工具：在 CI 里登录 chat.deepseek.com 并抓取真实 DOM 快照（用于皮肤适配开发）。
产物：/tmp/dump/*.html（outerHTML）、*.json（元素骨架+计算样式）、*.png（截图）、*_vars.json（CSS 变量）
不上传任何 cookie / storageState。
"""
import json
import os
import re
import time

from playwright.sync_api import sync_playwright

OUT = "/tmp/dump"
os.makedirs(OUT, exist_ok=True)
HOME = "https://chat.deepseek.com/"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36")
UA_M = ("Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36")

SKELETON = """() => {
  const out = [];
  const W = window.innerWidth, H = window.innerHeight;
  const walk = (el, depth) => {
    if (out.length > 15000) return;
    const tag = el.tagName;
    if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'LINK') return;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    const vis = cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0;
    if (vis && r.bottom > -50 && r.top < H + 50) {
      const cls = (el.getAttribute('class') || '').trim();
      const txt = (el.children.length === 0 ? (el.textContent || '').trim().slice(0, 40) : '');
      out.push({
        t: tag.toLowerCase(), d: depth,
        c: cls || undefined,
        id: el.id || undefined,
        role: el.getAttribute('role') || undefined,
        aria: el.getAttribute('aria-label') || undefined,
        title: el.getAttribute('title') || undefined,
        ph: el.getAttribute('placeholder') || undefined,
        href: el.getAttribute('href') || undefined,
        x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height),
        bg: cs.backgroundColor, fg: cs.color, bd: cs.borderColor,
        br: cs.borderRadius, bf: cs.backdropFilter !== 'none' ? cs.backdropFilter : undefined,
        fs: cs.fontSize, fw: cs.fontWeight,
        txt: txt || undefined
      });
    }
    for (const c of el.children) walk(c, depth + 1);
  };
  walk(document.body, 0);
  return {vw: W, vh: H, items: out};
}"""

VARS = """() => {
  const res = {};
  const pick = (rule) => {
    if (!rule.style) return;
    const sel = rule.selectorText || '';
    if (!sel.includes('root') && !sel.includes('body')) return;
    for (const p of rule.style) {
      if (p.startsWith('--')) res[p] = rule.style.getPropertyValue(p).trim();
    }
  };
  for (const sheet of document.styleSheets) {
    let rules = null;
    try { rules = sheet.cssRules; } catch (e) { continue; }
    if (!rules) continue;
    for (const r of rules) {
      pick(r);
      try { if (r.cssRules) for (const r2 of r.cssRules) pick(r2); } catch (e) {}
    }
  }
  return res;
}"""

CLICKABLES = """() => {
  const res = [];
  const nodes = document.querySelectorAll('button,[role="button"],a,[aria-label],svg');
  for (const el of nodes) {
    const r = el.getBoundingClientRect();
    if (r.width < 4 || r.height < 4) continue;
    res.push({
      t: el.tagName.toLowerCase(),
      c: (el.getAttribute('class') || '').slice(0, 120),
      aria: el.getAttribute('aria-label') || undefined,
      title: el.getAttribute('title') || undefined,
      txt: (el.textContent || '').trim().slice(0, 30) || undefined,
      x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height)
    });
    if (res.length > 400) break;
  }
  return res;
}"""


def save(pg, name):
    try:
        open(os.path.join(OUT, name + ".html"), "w", encoding="utf-8").write(
            pg.evaluate("document.documentElement.outerHTML"))
    except Exception as e:
        print("html fail", name, e)
    try:
        json.dump(pg.evaluate(SKELETON), open(os.path.join(OUT, name + ".json"), "w"),
                  ensure_ascii=False)
    except Exception as e:
        print("skeleton fail", name, e)
    try:
        json.dump(pg.evaluate(VARS), open(os.path.join(OUT, name + "_vars.json"), "w"),
                  ensure_ascii=False, indent=1)
    except Exception as e:
        print("vars fail", name, e)
    try:
        json.dump(pg.evaluate(CLICKABLES), open(os.path.join(OUT, name + "_click.json"), "w"),
                  ensure_ascii=False, indent=1)
    except Exception as e:
        print("click fail", name, e)
    try:
        pg.screenshot(path=os.path.join(OUT, name + ".png"))
    except Exception as e:
        print("shot fail", name, e)
    print("saved", name, "url=", pg.url)


def dismiss_overlays(pg):
    for pat in ["Accept", "Accept all", "同意", "接受", "Got it", "知道了", "OK"]:
        try:
            loc = pg.get_by_role("button", name=pat, exact=False)
            if loc.count() > 0:
                loc.first.click(timeout=2500)
                pg.wait_for_timeout(600)
                print("clicked overlay:", pat)
        except Exception:
            pass


def try_login(pg):
    phone = os.environ.get("DS_TEST_PHONE", "").strip()
    pwd = os.environ.get("DS_TEST_PASSWORD", "").strip()
    accounts = os.environ.get("DS_TEST_ACCOUNTS", "").strip()
    if not phone and accounts:
        try:
            arr = json.loads(accounts)
            if arr:
                phone = str(arr[0].get("phone") or arr[0].get("username") or "").strip()
                pwd = str(arr[0].get("password") or "").strip()
        except Exception as e:
            print("accounts parse fail", e)
    print("has phone:", bool(phone), "has pwd:", bool(pwd))
    if not phone or not pwd:
        return False

    # 已是登录态？
    if "sign_in" not in pg.url:
        return True

    # 切到密码登录（若有该 tab）
    for pat in ["Log in with password", "密码登录", "Password"]:
        try:
            loc = pg.get_by_text(pat, exact=False)
            if loc.count() > 0:
                loc.first.click(timeout=2500)
                pg.wait_for_timeout(800)
                print("switched to password mode:", pat)
                break
        except Exception:
            pass

    # 手机号输入
    filled = False
    for sel in ["input[type=tel]", "input[inputmode=numeric]", "input[placeholder*='phone' i]",
                "input[placeholder*='手机']", "input[type=text]"]:
        try:
            loc = pg.locator(sel)
            if loc.count() > 0:
                loc.first.click(timeout=2500)
                loc.first.fill("")
                loc.first.type(phone, delay=40)
                filled = True
                print("phone filled via", sel)
                break
        except Exception:
            pass
    # 密码
    try:
        loc = pg.locator("input[type=password]")
        if loc.count() > 0:
            loc.first.click(timeout=2500)
            loc.first.type(pwd, delay=40)
            print("pwd filled")
    except Exception as e:
        print("pwd fail", e)

    pg.wait_for_timeout(500)
    if not filled:
        save(pg, "00_signin_nofill")
        return False

    for pat in ["Log in", "登录", "Sign in"]:
        try:
            loc = pg.get_by_role("button", name=pat, exact=True)
            if loc.count() > 0:
                loc.first.click(timeout=3000)
                print("submit via", pat)
                break
        except Exception:
            pass

    for _ in range(40):
        pg.wait_for_timeout(1000)
        if "sign_in" not in pg.url:
            print("login ok ->", pg.url)
            return True
    print("login timeout, url=", pg.url)
    return False


def run():
    with sync_playwright() as p:
        b = p.chromium.launch(args=["--no-sandbox", "--disable-dev-shm-usage"])
        ctx = b.new_context(viewport={"width": 1440, "height": 900}, user_agent=UA,
                            locale="zh-CN", device_scale_factor=1)
        pg = ctx.new_page()
        pg.goto(HOME, wait_until="domcontentloaded", timeout=90000)
        pg.wait_for_timeout(4000)
        dismiss_overlays(pg)
        save(pg, "01_entry")
        ok = try_login(pg)
        if not ok:
            save(pg, "02_login_fail")
            b.close()
            return

        pg.wait_for_timeout(4000)
        dismiss_overlays(pg)
        save(pg, "10_home_light")

        # 侧栏（桌面默认展开；若收起则点开）
        try:
            pg.evaluate("""() => {
              const el = document.querySelector('[class*="sidebar"], aside');
              return !!el;
            }""")
        except Exception:
            pass
        save(pg, "11_home_light_after")

        # 发送一条测试消息
        try:
            ta = pg.locator("textarea").first
            ta.click(timeout=5000)
            ta.fill("皮肤适配测试：请回复一句「收到」。")
            pg.wait_for_timeout(400)
            ta.press("Enter")
            print("message sent")
            for _ in range(40):
                pg.wait_for_timeout(1000)
                if pg.evaluate("!!document.querySelector('.ds-markdown, [class*=\"markdown\"]')"):
                    break
            pg.wait_for_timeout(4000)
            save(pg, "20_chat_light")
        except Exception as e:
            print("send fail", e)
            save(pg, "20_chat_light_fail")

        # 打开设置（尽量多点几个可能入口，每个都 dump）
        for label in ["设置", "Settings", "个人设置", "账号设置"]:
            try:
                loc = pg.get_by_role("button", name=label, exact=False)
                if loc.count() > 0:
                    loc.first.click(timeout=3000)
                    pg.wait_for_timeout(1500)
                    save(pg, "30_settings_light")
                    break
            except Exception:
                pass
        try:
            pd = pg.locator("[class*='avatar'], img[alt*='avatar' i]")
            if pd.count() > 0 and not os.path.exists(os.path.join(OUT, "30_settings_light.html")):
                pd.first.click(timeout=3000)
                pg.wait_for_timeout(1500)
                save(pg, "30_settings_light")
        except Exception:
            pass

        # 深色
        try:
            pg.keyboard.press("Escape")
            pg.wait_for_timeout(500)
            pg.evaluate("""() => {
              localStorage.setItem('__appKit_@deepseek/chat_themePreference',
                JSON.stringify({value: 'dark', __version: '0'}));
            }""")
            pg.reload(wait_until="domcontentloaded")
            pg.wait_for_timeout(4000)
            save(pg, "40_home_dark")
            try:
                ta = pg.locator("textarea").first
                if ta.count() > 0:
                    save(pg, "41_home_dark_ta")
            except Exception:
                pass
        except Exception as e:
            print("dark fail", e)

        # 手机视口
        try:
            ctx2 = b.new_context(viewport={"width": 412, "height": 900}, user_agent=UA_M,
                                 locale="zh-CN", device_scale_factor=2,
                                 storage_state=ctx.storage_state())
            pg2 = ctx2.new_page()
            pg2.goto(HOME, wait_until="domcontentloaded", timeout=90000)
            pg2.wait_for_timeout(5000)
            dismiss_overlays(pg2)
            save(pg2, "50_mobile_light")
            try:
                pg2.locator("textarea").first.click(timeout=4000)
                pg2.wait_for_timeout(1200)
                save(pg2, "51_mobile_light_typing")
            except Exception as e:
                print("mobile ta fail", e)
            # 侧栏抽屉
            for label in ["打开侧边栏", "Toggle sidebar", "菜单", "Menu", "侧边栏"]:
                try:
                    loc = pg2.get_by_role("button", name=label, exact=False)
                    if loc.count() > 0:
                        loc.first.click(timeout=3000)
                        pg2.wait_for_timeout(1200)
                        save(pg2, "52_mobile_drawer")
                        break
                except Exception:
                    pass
        except Exception as e:
            print("mobile fail", e)

        b.close()


run()
