package com.hiweny.dswebapi;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.animation.Animation;
import android.view.animation.ScaleAnimation;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

public class MainActivity extends Activity {
    private static final int TAB_CONTROL = 0, TAB_WEB = 1;
    private static final int REQ_FILE = 1001;
    private static final int REQ_NOTIF = 1002;
    private static final String[] UA = new String[]{};

    private Theme theme;
    private LinearLayout rootView, bottomNav;
    private ScrollView controlScroll;
    private LinearLayout webPanel;
    private FrameLayout webContainer;
    private LinearLayout accountBar, accountChips;
    private TextView tvAccounts;
    private boolean addingMode = false;
    private String pendingAddId = "";
    private String accountSig = "";
    private View controlPanel;
    private int currentTab = TAB_CONTROL;

    private TextView tvService, tvWeb, tvLogin, tvCalls, tvCtx, tvLast, tvBase, tvKey, tvLog;
    private LinearLayout navIconBoxControl, navIconBoxWeb;
    private TextView navIconControl, navIconWeb, navLabelControl, navLabelWeb;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private long lastBack = 0;

    private ValueCallback<Uri[]> filePathCallback;

    private final Runnable statsTask = new Runnable() {
        @Override public void run() {
            refreshStats();
            handler.postDelayed(this, 2000L);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        theme = Theme.of(this);
        enableEdgeToEdge();
        buildUi();
        WebHost.setActivity(this);
        WebHost.setDark(theme.dark);
        WebHost.setFileChooser(this::onShowFileChooser);
        ApiService.start(this);
        requestNotifPermission();
        requestBatteryWhitelist();
        if (Util.prefs(this).getBoolean("keep_screen", true)) {
            getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        }
        handler.post(attachWebRunnable);
        handler.postDelayed(statsTask, 1500L);
        AccountPool.get().ensureLoaded(this);
        WebHost.ensure(this, WebHost.loadBridgeJs(this));
    }

    private final Runnable attachWebRunnable = new Runnable() {
        int tries = 0;
        @Override public void run() {
            if (WebHost.get() != null) {
                if (webPanel != null) WebHost.attachTo(webContainer);
            } else if (tries++ < 20) {
                handler.postDelayed(this, 300L);
            }
        }
    };

    /* ================= 窗口 / 主题 ================= */

    @SuppressWarnings("deprecation")
    private void enableEdgeToEdge() {
        Window w = getWindow();
        View decor = w.getDecorView();
        decor.setSystemUiVisibility(View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
        w.setStatusBarColor(android.graphics.Color.TRANSPARENT);
        w.setNavigationBarColor(android.graphics.Color.TRANSPARENT);
        applyBarIcons();
    }

    @SuppressWarnings("deprecation")
    private void applyBarIcons() {
        View decor = getWindow().getDecorView();
        int flags = View.SYSTEM_UI_FLAG_LAYOUT_STABLE | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION;
        if (!theme.dark) flags |= View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
        decor.setSystemUiVisibility(flags);
    }

    @Override
    public void onConfigurationChanged(android.content.res.Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        boolean nowDark = (newConfig.uiMode & android.content.res.Configuration.UI_MODE_NIGHT_MASK)
                == android.content.res.Configuration.UI_MODE_NIGHT_YES;
        if (nowDark != theme.dark) {
            theme = Theme.of(this);
            WebHost.setDark(theme.dark);
            buildUi();
            WebHost.attachTo(webContainer);
            handler.post(attachWebRunnable);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (webPanel != null) WebHost.attachTo(webContainer);
        refreshStats();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        handler.removeCallbacksAndMessages(null);
        WebHost.setActivity(null);
        WebHost.detachFromParent();
    }

    @Override
    public void onBackPressed() {
        if (currentTab == TAB_WEB) { switchTab(TAB_CONTROL); return; }
        long now = System.currentTimeMillis();
        if (now - lastBack < 2000) { super.onBackPressed(); moveTaskToBack(true); }
        else { lastBack = now; toast("再按一次返回键退出（服务继续后台运行）"); }
    }

    /* ================= UI 构建 ================= */

    private int dp(int v) { return Math.round(v * getResources().getDisplayMetrics().density); }

    private GradientDrawable roundedBg(int color, int radiusDp) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(color);
        g.setCornerRadius(dp(radiusDp));
        return g;
    }
    private GradientDrawable strokeBg(int color, int stroke, int radiusDp) {
        GradientDrawable g = roundedBg(color, radiusDp);
        g.setStroke(Math.max(1, dp(1)), stroke);
        return g;
    }

    private void buildUi() {
        rootView = new LinearLayout(this);
        rootView.setOrientation(LinearLayout.VERTICAL);
        rootView.setBackgroundColor(theme.bg());
        FrameLayout container = new FrameLayout(this);
        controlPanel = buildControlPanel();
        container.addView(controlPanel, new FrameLayout.LayoutParams(-1, -1));
        webPanel = new LinearLayout(this);
        webPanel.setOrientation(LinearLayout.VERTICAL);
        webPanel.setBackgroundColor(theme.bg());
        webPanel.setVisibility(View.GONE);
        accountBar = buildAccountBar();
        webPanel.addView(accountBar, new LinearLayout.LayoutParams(-1, -2));
        webContainer = new FrameLayout(this);
        webPanel.addView(webContainer, new LinearLayout.LayoutParams(-1, 0, 1f));
        container.addView(webPanel, new FrameLayout.LayoutParams(-1, -1));
        rootView.addView(container, new LinearLayout.LayoutParams(-1, 0, 1f));
        rootView.addView(buildBottomNav(), new LinearLayout.LayoutParams(-1, -2));
        setContentView(rootView);
        applyWindowInsets();
        updateNavStyle();
    }

    @SuppressWarnings("deprecation")
    private void applyWindowInsets() {
        if (rootView == null) return;
        rootView.setOnApplyWindowInsetsListener((v, insets) -> {
            int top = insets.getSystemWindowInsetTop();
            int bottom = insets.getSystemWindowInsetBottom();
            if (controlScroll != null) {
                controlScroll.setPadding(dp(14), top + dp(12), dp(14), dp(12));
                controlScroll.setClipToPadding(false);
            }
            if (bottomNav != null) bottomNav.setPadding(0, 0, 0, bottom);
            return insets;
        });
        rootView.requestApplyInsets();
    }

    private View buildBottomNav() {
        bottomNav = new LinearLayout(this);
        bottomNav.setOrientation(LinearLayout.VERTICAL);
        bottomNav.setBackgroundColor(theme.headerBg());
        bottomNav.setElevation(dp(10));
        View divider = new View(this);
        divider.setBackgroundColor(theme.divider());
        bottomNav.addView(divider, new LinearLayout.LayoutParams(-1, Math.max(1, dp(1))));
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setPadding(dp(24), dp(8), dp(24), dp(10));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, 1f);
        LinearLayout c = buildNavItem("\uD83C\uDF9B", "控制台", v -> switchTab(TAB_CONTROL));
        LinearLayout w = buildNavItem("\uD83D\uDCAC", "对话页", v -> switchTab(TAB_WEB));
        row.addView(c, lp);
        row.addView(w, lp);
        bottomNav.addView(row, new LinearLayout.LayoutParams(-1, -2));
        return bottomNav;
    }

    private LinearLayout buildNavItem(String icon, String label, final View.OnClickListener onClick) {
        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.VERTICAL);
        col.setGravity(Gravity.CENTER_HORIZONTAL);
        final LinearLayout pill = new LinearLayout(this);
        pill.setOrientation(LinearLayout.VERTICAL);
        pill.setGravity(Gravity.CENTER);
        pill.setPadding(dp(22), dp(5), dp(22), dp(5));
        GradientDrawable g = roundedBg(theme.accent(), 20);
        g.setAlpha(0);
        pill.setBackground(g);
        TextView ic = new TextView(this);
        ic.setText(icon);
        ic.setTextSize(21f);
        pill.addView(ic);
        col.addView(pill);
        TextView lb = new TextView(this);
        lb.setText(label);
        lb.setTextSize(11.5f);
        lb.setGravity(Gravity.CENTER);
        lb.setPadding(0, dp(3), 0, 0);
        col.addView(lb);
        if ("\uD83C\uDF9B".equals(icon)) { navIconBoxControl = pill; navIconControl = ic; navLabelControl = lb; }
        else { navIconBoxWeb = pill; navIconWeb = ic; navLabelWeb = lb; }
        col.setOnClickListener(v -> { onClick.onClick(v); animateNavPill(pill); });
        return col;
    }

    private void animateNavPill(LinearLayout pill) {
        ScaleAnimation a = new ScaleAnimation(0.7f, 1f, 0.7f, 1f, 1, 0.5f, 1, 0.5f);
        a.setDuration(200);
        pill.startAnimation(a);
    }

    private void switchTab(int tab) {
        currentTab = tab;
        boolean control = tab == TAB_CONTROL;
        controlPanel.setVisibility(control ? View.VISIBLE : View.GONE);
        webPanel.setVisibility(control ? View.GONE : View.VISIBLE);
        if (!control) WebHost.attachTo(webContainer);
        updateNavStyle();
        if (control) refreshStats();
    }

    private void updateNavStyle() {
        boolean c = currentTab == TAB_CONTROL;
        styleNav(navIconBoxControl, navLabelControl, c);
        styleNav(navIconBoxWeb, navLabelWeb, !c);
    }

    private void styleNav(LinearLayout pill, TextView label, boolean active) {
        if (pill == null || pill.getBackground() == null) return;
        ((GradientDrawable) pill.getBackground()).setAlpha(active ? 40 : 0);
        label.setTextColor(active ? theme.accent() : theme.textSub());
        label.setTypeface(null, active ? Typeface.BOLD : Typeface.NORMAL);
    }

    /* ================= 控制面板 ================= */

    private LinearLayout card() {
        LinearLayout c = new LinearLayout(this);
        c.setOrientation(LinearLayout.VERTICAL);
        c.setBackground(strokeBg(theme.cardBg(), theme.divider(), 16));
        c.setPadding(dp(16), dp(14), dp(16), dp(14));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, -2);
        lp.setMargins(0, dp(6), 0, dp(6));
        c.setLayoutParams(lp);
        return c;
    }

    private TextView cardTitle(String s) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(13.5f);
        t.setTextColor(theme.accent());
        t.setTypeface(null, Typeface.BOLD);
        t.setPadding(0, 0, 0, dp(8));
        return t;
    }

    private TextView line(String label, String value) {
        TextView t = new TextView(this);
        t.setText(label + "：" + value);
        t.setTextSize(13.5f);
        t.setTextColor(theme.text());
        t.setPadding(0, dp(3), 0, dp(3));
        return t;
    }

    private View buildControlPanel() {
        controlScroll = new ScrollView(this);
        controlScroll.setBackgroundColor(theme.bg());
        controlScroll.setFillViewport(true);
        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.VERTICAL);
        // 平板适配：内容最宽 760dp 并居中，避免一行拉得过长
        int scrW = getResources().getDisplayMetrics().widthPixels - dp(28);
        FrameLayout wrap = new FrameLayout(this);
        controlScroll.addView(wrap, new LinearLayout.LayoutParams(-1, -2));
        wrap.addView(col, new FrameLayout.LayoutParams(Math.min(scrW, dp(760)), -2, Gravity.CENTER_HORIZONTAL));

        // 标题
        TextView h = new TextView(this);
        h.setText("DeepSeek Web API");
        h.setTextSize(20f);
        h.setTextColor(theme.text());
        h.setTypeface(null, Typeface.BOLD);
        col.addView(h);
        TextView sub = new TextView(this);
        sub.setText("把 chat.deepseek.com 官网封装成本机 OpenAI 兼容接口");
        sub.setTextSize(12f);
        sub.setTextColor(theme.textSub());
        sub.setPadding(0, dp(2), 0, dp(6));
        col.addView(sub);

        // 状态
        LinearLayout st = card();
        st.addView(cardTitle("运行状态"));
        tvService = line("服务", "—"); st.addView(tvService);
        tvWeb = line("网页", "—"); st.addView(tvWeb);
        tvLogin = line("登录", "—"); st.addView(tvLogin);
        tvCalls = line("调用", "—"); st.addView(tvCalls);
        tvCtx = line("会话上下文", "—"); st.addView(tvCtx);
        tvLast = line("最近", "—"); st.addView(tvLast);
        col.addView(st);

        // 接口
        LinearLayout api = card();
        api.addView(cardTitle("接口信息"));
        tvBase = line("Base URL", "—"); api.addView(tvBase);
        tvKey = line("API Key", "—"); api.addView(tvKey);
        api.addView(rowButtons(new String[][]{
                {"复制 Base URL", "copy_base"}, {"复制 API Key", "copy_key"}
        }));
        col.addView(api);

        // 设置
        LinearLayout set = card();
        set.addView(cardTitle("设置"));
        set.addView(makeRow("端口", "port"));
        set.addView(makeRow("API Key", "apikey"));
        set.addView(makeRow("超时（秒）", "timeout"));
        set.addView(makeRowCycle("思考策略", "thinking_mode", new String[]{"auto", "on", "off"}, "跟随/强制开/强制关"));
        set.addView(makeRowCycle("搜索策略", "search_mode", new String[]{"auto", "on", "off"}, "跟随/强制开/强制关"));
        set.addView(makeSwitch("无状态模式", "每次发送完整对话（不依赖官网会话记忆）", "stateless"));
        set.addView(makeSwitch("自动新开对话", "上下文达阈值时自动新建对话，并尽力删除旧会话", "auto_newchat"));
        set.addView(makeRow("上下文上限（tokens）", "context_tokens"));
        set.addView(makeRow("新对话阈值（%）", "newchat_threshold"));
        set.addView(makeSwitch("保持屏幕常亮", "提高后台存活率（可关）", "keep_screen"));
        set.addView(makeSwitch("悬浮球（快捷入口 / 助保活）", "屏幕上常驻小圆球，AI 回复时有动效；点击回到 App", "floating_ball"));
        col.addView(set);

        // 多账号
        LinearLayout accCard = card();
        accCard.addView(cardTitle("多账号轮换"));
        tvAccounts = line("账号", "—");
        accCard.addView(tvAccounts);
        Switch rot = new Switch(this);
        rot.setText("启用轮换（严格顺序，用完一个换下一个）");
        rot.setTextSize(13f);
        rot.setTextColor(theme.text());
        rot.setPadding(0, dp(4), 0, dp(4));
        rot.setChecked(AccountVault.rotateEnabled(this));
        rot.setOnCheckedChangeListener((b, checked) -> {
            AccountVault.setRotateEnabled(MainActivity.this, checked);
            toast(checked ? "轮换已开启：按顺序轮流使用各账号" : "轮换已关闭：只用当前账号");
            accountSig = "";
            refreshAccountBar();
        });
        accCard.addView(rot);
        accCard.addView(makeVaultRow("每账号连发次数", "limit"));
        accCard.addView(makeVaultRow("限流冷却（分钟）", "cooldown"));
        accCard.addView(rowButtons(new String[][]{{"去对话页管理账号", "goto_accounts"}}));
        col.addView(accCard);

        // 操作
        LinearLayout act = card();
        act.addView(cardTitle("操作"));
        act.addView(rowButtons(new String[][]{
                {"启动服务", "start_svc"}, {"停止服务", "stop_svc"}
        }));
        act.addView(rowButtons(new String[][]{
                {"新建对话", "new_chat"}, {"重载网页", "reload"}
        }));
        act.addView(rowButtons(new String[][]{
                {"无障碍保活设置", "access"}, {"电池优化白名单", "battery"}
        }));
        act.addView(rowButtons(new String[][]{
                {"通知权限", "notif"}, {"清空调试日志", "clearlog"}
        }));
        col.addView(act);

        // 日志
        LinearLayout logCard = card();
        logCard.addView(cardTitle("运行日志"));
        tvLog = new TextView(this);
        tvLog.setTextSize(11.5f);
        tvLog.setTextColor(theme.textSub());
        tvLog.setLineSpacing(0, 1.15f);
        logCard.addView(tvLog);
        col.addView(logCard);

        return controlScroll;
    }

    private LinearLayout makeRow(final String name, final String key) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, dp(7), 0, dp(7));
        TextView t = new TextView(this);
        t.setText(name);
        t.setTextSize(13.5f);
        t.setTextColor(theme.text());
        t.setLayoutParams(new LinearLayout.LayoutParams(0, -2, 1f));
        row.addView(t);
        TextView v = new TextView(this);
        v.setText(String.valueOf(currentSetting(key)));
        v.setTextSize(13.5f);
        v.setTextColor(theme.accent());
        v.setTag(key);
        row.addView(v);
        row.setOnClickListener(x -> editSetting(name, key, (TextView) v));
        return row;
    }

    private LinearLayout makeRowCycle(final String name, final String key, final String[] values, final String hint) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, dp(7), 0, dp(7));
        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.VERTICAL);
        col.setLayoutParams(new LinearLayout.LayoutParams(0, -2, 1f));
        TextView t = new TextView(this);
        t.setText(name); t.setTextSize(13.5f); t.setTextColor(theme.text());
        col.addView(t);
        TextView h = new TextView(this);
        h.setText(hint); h.setTextSize(11f); h.setTextColor(theme.textSub());
        col.addView(h);
        row.addView(col);
        final TextView v = new TextView(this);
        v.setText(String.valueOf(currentSetting(key)));
        v.setTextSize(13.5f);
        v.setTextColor(theme.accent());
        row.addView(v);
        row.setOnClickListener(x -> {
            String cur = String.valueOf(currentSetting(key));
            int idx = 0;
            for (int i = 0; i < values.length; i++) if (values[i].equals(cur)) idx = i;
            String next = values[(idx + 1) % values.length];
            putSetting(key, next);
            v.setText(next);
            DeepSeekController.get().configure(
                    String.valueOf(currentSetting("thinking_mode")), String.valueOf(currentSetting("search_mode")));
            toast(name + " → " + next);
        });
        return row;
    }

    private View makeSwitch(final String name, String desc, final String key) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, dp(6), 0, dp(6));
        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.VERTICAL);
        col.setLayoutParams(new LinearLayout.LayoutParams(0, -2, 1f));
        TextView t = new TextView(this);
        t.setText(name); t.setTextSize(13.5f); t.setTextColor(theme.text());
        col.addView(t);
        TextView h = new TextView(this);
        h.setText(desc); h.setTextSize(11f); h.setTextColor(theme.textSub());
        col.addView(h);
        row.addView(col);
        Switch sw = new Switch(this);
        boolean defOn = "keep_screen".equals(key) || "stateless".equals(key) || "auto_newchat".equals(key);
        sw.setChecked(Util.prefs(this).getBoolean(key, defOn));
        sw.setOnCheckedChangeListener((b, checked) -> {
            Util.prefs(this).edit().putBoolean(key, checked).apply();
            if ("keep_screen".equals(key)) {
                if (checked) getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
                else getWindow().clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            }
            if ("floating_ball".equals(key)) {
                if (checked) {
                    if (!ApiService.canOverlay(MainActivity.this)) {
                        Toast.makeText(MainActivity.this, "请先授予「悬浮窗」权限，授权后重新打开本开关", Toast.LENGTH_LONG).show();
                        try {
                            startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                                    Uri.parse("package:" + getPackageName())));
                        } catch (Exception ignored) {}
                        sw.setChecked(false);   // 待授权后再开
                        return;
                    }
                    ApiService.start(MainActivity.this);
                    ApiService svc = ApiService.instance();
                    if (svc != null) svc.showFloatingBall();
                } else {
                    ApiService svc = ApiService.instance();
                    if (svc != null) svc.hideFloatingBall();
                }
            }
        });
        row.addView(sw);
        return row;
    }

    private LinearLayout rowButtons(String[][] items) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setPadding(0, dp(6), 0, 0);
        for (String[] it : items) {
            TextView b = new TextView(this);
            b.setText(it[0]);
            b.setTextSize(13f);
            b.setTextColor(theme.text());
            b.setGravity(Gravity.CENTER);
            b.setPadding(dp(10), dp(10), dp(10), dp(10));
            b.setBackground(strokeBg(theme.btnBg(), theme.btnStroke(), 12));
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, 1f);
            lp.setMargins(dp(3), 0, dp(3), 0);
            b.setLayoutParams(lp);
            final String action = it[1];
            b.setOnClickListener(v -> onAction(action));
            row.addView(b);
        }
        return row;
    }

    /* ================= 设置读写 ================= */

    private Object currentSetting(String key) {
        android.content.SharedPreferences sp = Util.prefs(this);
        switch (key) {
            case "port": return sp.getInt("port", 8787);
            case "apikey": return sp.getString("api_key", "sk-deepseek");
            case "timeout": return sp.getInt("timeout", 300);
            case "thinking_mode": return sp.getString("thinking_mode", "on");
            case "search_mode": return sp.getString("search_mode", "off");
            case "context_tokens": return sp.getInt("context_tokens", 1000000);
            case "newchat_threshold": return sp.getInt("newchat_threshold", 70);
        }
        return "";
    }

    private void putSetting(String key, String value) {
        android.content.SharedPreferences.Editor e = Util.prefs(this).edit();
        if ("port".equals(key)) { try { e.putInt("port", Integer.parseInt(value)); e.remove("port_active"); } catch (Exception ignored) {} }
        else if ("timeout".equals(key)) { try { e.putInt("timeout", Integer.parseInt(value)); } catch (Exception ignored) {} }
        else if ("context_tokens".equals(key)) { try { e.putInt("context_tokens", Integer.parseInt(value)); } catch (Exception ignored) {} }
        else if ("newchat_threshold".equals(key)) { try { e.putInt("newchat_threshold", Integer.parseInt(value)); } catch (Exception ignored) {} }
        else e.putString(key, value);
        e.apply();
    }

    private void editSetting(final String name, final String key, final TextView view) {
        final EditText et = new EditText(this);
        et.setText(String.valueOf(currentSetting(key)));
        et.setTextColor(theme.text());
        et.setInputType(("port".equals(key) || "timeout".equals(key) || "context_tokens".equals(key)
                || "newchat_threshold".equals(key)) ? InputType.TYPE_CLASS_NUMBER : InputType.TYPE_CLASS_TEXT);
        new AlertDialog.Builder(this).setTitle("修改 " + name).setView(et)
                .setPositiveButton("保存", (d, w) -> {
                    String v = et.getText().toString().trim();
                    putSetting(key, v);
                    view.setText(v);
                    if ("port".equals(key)) { toast("端口已修改，重启服务后生效"); }
                    else if ("apikey".equals(key)) { toast("API Key 已修改"); }
                    refreshStats();
                })
                .setNegativeButton("取消", null).show();
    }

    /* ================= 操作 ================= */

    private void onAction(String action) {
        switch (action) {
            case "copy_base": copy("http://127.0.0.1:" + currentSetting("port") + "/v1"); break;
            case "copy_key": copy(String.valueOf(currentSetting("apikey"))); break;
            case "start_svc": ApiService.start(this); toast("正在启动服务…"); handler.postDelayed(this::refreshStats, 800); break;
            case "stop_svc": ApiService.stop(this); toast("服务已停止"); handler.postDelayed(this::refreshStats, 800); break;
            case "new_chat": doNewChat(); break;
            case "reload": WebHost.reload(); toast("正在重载网页…"); break;
            case "access": openAccessibility(); break;
            case "battery": requestBatteryWhitelist(); break;
            case "notif": requestNotifPermission(); break;
            case "clearlog": Util.clearLog(); refreshStats(); toast("日志已清空"); break;
            case "goto_accounts": switchTab(TAB_WEB); toast("顶部就是账号栏，点「＋ 添加」新增"); break;
        }
    }

    private void doNewChat() {
        ApiService.resetContext();
        new Thread(() -> {
            JSONObject r = DeepSeekController.get().newChat();
            handler.post(() -> toast(r.optBoolean("ok") ? "已新建对话（上下文计数已重置）" : "新建对话失败（请在对话页手动操作）"));
        }).start();
    }

    private void copy(String s) {
        android.content.ClipboardManager cm = (android.content.ClipboardManager) getSystemService(CLIPBOARD_SERVICE);
        cm.setPrimaryClip(android.content.ClipData.newPlainText("ds", s));
        toast("已复制: " + s);
    }

    private void openAccessibility() {
        try {
            startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
            toast("请开启「API 桥保活服务」以获得更强后台存活");
        } catch (Exception e) { toast("无法打开无障碍设置"); }
    }

    private void requestBatteryWhitelist() {
        try {
            if (Build.VERSION.SDK_INT >= 23) {
                android.os.PowerManager pm = (android.os.PowerManager) getSystemService(POWER_SERVICE);
                if (!pm.isIgnoringBatteryOptimizations(getPackageName())) {
                    Intent i = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS);
                    i.setData(Uri.parse("package:" + getPackageName()));
                    startActivity(i);
                }
            }
        } catch (Exception ignored) {}
    }

    private void requestNotifPermission() {
        if (Build.VERSION.SDK_INT >= 33) {
            if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, REQ_NOTIF);
            }
        }
    }

    /* ================= 状态刷新 ================= */

    private void refreshStats() {
        if (tvService == null) return;
        tvService.setText(ApiService.serviceRunning ? ApiService.state() : "未运行（点击启动服务）");
        tvWeb.setText(WebHost.isReady() ? "已加载 chat.deepseek.com" : (WebHost.get() != null ? "加载中…" : "未初始化"));
        tvLogin.setText(ApiService.isLoggedInText());
        tvCalls.setText("总 " + ApiService.totalCallsText());
        tvCtx.setText(ApiService.contextInfoText());
        tvLast.setText(ApiService.lastCallText());
        tvBase.setText("http://127.0.0.1:" + currentSetting("port") + "/v1");
        tvKey.setText(String.valueOf(currentSetting("apikey")));
        if (tvLog != null) tvLog.setText(Util.logText());
        if (tvAccounts != null) {
            List<AccountSlot> ls = AccountPool.get().snapshot();
            if (ls.isEmpty()) {
                tvAccounts.setText("尚未添加（到「对话页」顶部点「＋ 添加」）");
            } else {
                AccountSlot a = AccountPool.get().find(AccountVault.activeId(this));
                tvAccounts.setText(ls.size() + " 个 · 当前：" + (a == null ? "—" : a.name)
                        + " · " + (AccountVault.rotateEnabled(this) ? "轮换开" : "轮换关")
                        + " · 连发上限 " + AccountVault.sendLimit(this));
            }
        }
        refreshAccountBar();
    }

    /* ================= 文件选择 ================= */

    private boolean onShowFileChooser(ValueCallback<Uri[]> cb, WebChromeClient.FileChooserParams params) {
        if (filePathCallback != null) { filePathCallback.onReceiveValue(null); }
        filePathCallback = cb;
        try {
            Intent intent = params.createIntent();
            startActivityForResult(intent, REQ_FILE);
            return true;
        } catch (Exception e) {
            filePathCallback = null;
            return false;
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQ_FILE) {
            if (filePathCallback == null) return;
            Uri[] results = null;
            if (resultCode == RESULT_OK && data != null) {
                if (data.getClipData() != null) {
                    int n = data.getClipData().getItemCount();
                    List<Uri> list = new ArrayList<>();
                    for (int i = 0; i < n; i++) list.add(data.getClipData().getItemAt(i).getUri());
                    results = list.toArray(new Uri[0]);
                } else if (data.getData() != null) {
                    results = new Uri[]{data.getData()};
                }
            }
            filePathCallback.onReceiveValue(results);
            filePathCallback = null;
        }
    }


    /* ================= 多账号顶栏 ================= */

    private boolean isTablet() {
        return getResources().getConfiguration().smallestScreenWidthDp >= 600;
    }

    private LinearLayout buildAccountBar() {
        LinearLayout bar = new LinearLayout(this);
        bar.setOrientation(LinearLayout.VERTICAL);
        bar.setBackgroundColor(theme.headerBg());

        HorizontalScrollView hs = new HorizontalScrollView(this);
        hs.setHorizontalScrollBarEnabled(false);
        hs.setOverScrollMode(View.OVER_SCROLL_NEVER);
        accountChips = new LinearLayout(this);
        accountChips.setOrientation(LinearLayout.HORIZONTAL);
        accountChips.setGravity(Gravity.CENTER_VERTICAL);
        accountChips.setPadding(dp(6), dp(6), dp(6), dp(6));
        hs.addView(accountChips, new LinearLayout.LayoutParams(-2, -2));
        bar.addView(hs, new LinearLayout.LayoutParams(-1, -2));

        View div = new View(this);
        div.setBackgroundColor(theme.divider());
        bar.addView(div, new LinearLayout.LayoutParams(-1, Math.max(1, dp(1))));
        return bar;
    }

    private TextView makeChip(String text, boolean active) {
        TextView t = new TextView(this);
        t.setText(text);
        t.setTextSize(isTablet() ? 13.5f : 12.5f);
        t.setSingleLine(true);
        t.setGravity(Gravity.CENTER);
        t.setPadding(dp(12), 0, dp(12), 0);
        t.setTextColor(active ? 0xFFFFFFFF : theme.text());
        t.setBackground(active ? roundedBg(theme.accent(), 18)
                : strokeBg(theme.cardBg(), theme.divider(), 18));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, dp(isTablet() ? 38 : 32));
        lp.setMargins(dp(4), 0, dp(4), 0);
        t.setLayoutParams(lp);
        return t;
    }

    private String accountSignature() {
        StringBuilder sb = new StringBuilder();
        String active = AccountVault.activeId(this);
        for (AccountSlot s : AccountPool.get().snapshot()) {
            sb.append(s.id).append(s.name).append(s.stateText()).append(s.enabled ? 1 : 0).append('|');
        }
        sb.append("A=").append(active).append("|add=").append(addingMode ? pendingAddId : "")
          .append("|n=").append(AccountPool.get().size());
        return sb.toString();
    }

    private void refreshAccountBar() {
        if (accountChips == null) return;
        AccountPool.get().ensureLoaded(this);
        String sig = accountSignature();
        if (sig.equals(accountSig)) return;
        accountSig = sig;
        accountChips.removeAllViews();

        String activeId = AccountVault.activeId(this);
        for (final AccountSlot s : AccountPool.get().snapshot()) {
            boolean active = s.id.equals(activeId);
            String label = s.name;
            if (!s.enabled) label += " · 停用";
            else if (s.inCooldown()) label += " · 冷却 " + AccountSlot.fmtLeft(s.cooldownLeftSec());
            else if (!s.loggedIn) label += " · 未登录";
            else label += " · " + s.sentInSession + "/" + AccountVault.sendLimit(this);
            TextView chip = makeChip(label, active);
            chip.setOnClickListener(v -> onChipTap(s));
            chip.setOnLongClickListener(v -> { showAccountMenu(s); return true; });
            accountChips.addView(chip);
        }

        TextView add = makeChip(addingMode ? "＋ 登录中…" : "＋ 添加", false);
        add.setOnClickListener(v -> { if (!addingMode) addAccountFlow(); });
        accountChips.addView(add);

        if (addingMode) {
            TextView save = makeChip("保存登录", true);
            save.setOnClickListener(v -> confirmSaveLogin());
            accountChips.addView(save);
            TextView cancel = makeChip("取消", false);
            cancel.setOnClickListener(v -> cancelAdd());
            accountChips.addView(cancel);
        }
    }

    private void onChipTap(final AccountSlot s) {
        if (addingMode) { toast("请先点「保存登录」或「取消」，再切换账号"); return; }
        final String activeId = AccountVault.activeId(this);
        if (s.id.equals(activeId)) { toast("当前已是「" + s.name + "」"); return; }
        toast("正在切换到「" + s.name + "」…");
        new Thread(() -> {
            AccountSlot cur = AccountPool.get().find(activeId);
            boolean ok = AccountSwitcher.switchTo(s, cur, 25000L);
            if (ok) {
                AccountVault.setActiveId(MainActivity.this, s.id);
                AccountPool.get().persist();
                handler.post(() -> { toast("已切换到「" + s.name + "」"); accountSig = ""; refreshAccountBar(); });
            } else {
                handler.post(() -> {
                    toast("「" + s.name + "」登录态失效，长按它选「重新登录」");
                    s.loggedIn = false;
                    accountSig = "";
                    refreshAccountBar();
                });
            }
        }).start();
    }

    private void addAccountFlow() {
        final EditText et = new EditText(this);
        et.setHint("账号备注，如：账号 2");
        et.setTextColor(theme.text());
        new AlertDialog.Builder(this)
                .setTitle("添加账号")
                .setMessage("确定后会清空当前页面的登录态，请在页面上登录新账号；登录完成后点顶部「保存登录」。")
                .setView(et)
                .setPositiveButton("开始登录", (d, w) -> {
                    String name = et.getText().toString().trim();
                    if (name.isEmpty()) name = "账号 " + (AccountPool.get().size() + 1);
                    AccountSlot cur = AccountPool.get().find(AccountVault.activeId(MainActivity.this));
                    AccountSwitcher.snapshot(cur);
                    AccountPool pool = AccountPool.get();
                    AccountSlot s = pool.add(name);
                    pool.persist();
                    pendingAddId = s.id;
                    addingMode = true;
                    accountSig = "";
                    refreshAccountBar();
                    new Thread(() -> AccountSwitcher.prepareForLogin()).start();
                    toast("请登录「" + name + "」，完成后点顶部「保存登录」");
                })
                .setNegativeButton("取消", null).show();
    }

    private void confirmSaveLogin() {
        final AccountSlot s = AccountPool.get().find(pendingAddId);
        if (s == null) { addingMode = false; pendingAddId = ""; accountSig = ""; refreshAccountBar(); return; }
        toast("正在保存登录态…");
        new Thread(() -> {
            boolean logged = AccountSwitcher.awaitReady(20000L);
            if (logged) AccountSwitcher.bindCurrent(s);
            final boolean ok = logged && s.hasCookie();
            if (ok) {
                AccountVault.setActiveId(MainActivity.this, s.id);
                s.loggedIn = true;
                AccountPool.get().persist();
            }
            handler.post(() -> {
                addingMode = false;
                pendingAddId = "";
                accountSig = "";
                toast(ok ? ("已保存「" + s.name + "」") : "还没检测到登录，请先完成登录再点保存");
                refreshAccountBar();
            });
        }).start();
    }

    private void cancelAdd() {
        final AccountSlot cur = AccountPool.get().find(AccountVault.activeId(this));
        AccountSlot s = AccountPool.get().find(pendingAddId);
        if (s != null && !s.hasCookie()) AccountPool.get().remove(s.id);
        addingMode = false;
        pendingAddId = "";
        accountSig = "";
        refreshAccountBar();
        if (cur != null && cur.hasCookie()) {
            new Thread(() -> AccountSwitcher.switchTo(cur, null, 30000L)).start();
        }
    }

    private void showAccountMenu(final AccountSlot s) {
        final String[] items = {"设为当前账号", "重新登录（清除登录态）", "重命名",
                s.enabled ? "停用该账号" : "启用该账号", "删除该账号"};
        new AlertDialog.Builder(this).setTitle(s.name).setItems(items, (d, which) -> {
            switch (which) {
                case 0: onChipTap(s); break;
                case 1: reloginAccount(s); break;
                case 2: renameAccount(s); break;
                case 3:
                    AccountPool.get().setEnabled(s.id, !s.enabled);
                    accountSig = ""; refreshAccountBar();
                    toast(s.enabled ? "已启用" : "已停用");
                    break;
                case 4: removeAccount(s); break;
            }
        }).show();
    }

    private void reloginAccount(final AccountSlot s) {
        new AlertDialog.Builder(this).setTitle("重新登录「" + s.name + "」")
                .setMessage("会清空该账号的登录态并回到登录页，登录完成后点顶部「保存登录」。")
                .setPositiveButton("开始", (d, w) -> {
                    s.cookies = "";
                    s.loggedIn = false;
                    AccountPool.get().persist();
                    AccountVault.setActiveId(MainActivity.this, s.id);
                    addingMode = true;
                    pendingAddId = s.id;
                    accountSig = "";
                    refreshAccountBar();
                    new Thread(() -> AccountSwitcher.prepareForLogin()).start();
                }).setNegativeButton("取消", null).show();
    }

    private void renameAccount(final AccountSlot s) {
        final EditText et = new EditText(this);
        et.setText(s.name);
        et.setTextColor(theme.text());
        new AlertDialog.Builder(this).setTitle("重命名").setView(et)
                .setPositiveButton("保存", (d, w) -> {
                    AccountPool.get().rename(s.id, et.getText().toString());
                    accountSig = "";
                    refreshAccountBar();
                }).setNegativeButton("取消", null).show();
    }

    private void removeAccount(final AccountSlot s) {
        if (AccountPool.get().size() <= 1) { toast("至少保留一个账号"); return; }
        new AlertDialog.Builder(this).setTitle("删除「" + s.name + "」？")
                .setMessage("只删除本机保存的登录快照，不影响 DeepSeek 账号本身。")
                .setPositiveButton("删除", (d, w) -> {
                    AccountPool.get().remove(s.id);
                    if (s.id.equals(AccountVault.activeId(MainActivity.this))) {
                        List<AccountSlot> l = AccountPool.get().snapshot();
                        if (!l.isEmpty()) AccountVault.setActiveId(MainActivity.this, l.get(0).id);
                    }
                    accountSig = "";
                    refreshAccountBar();
                    toast("已删除");
                }).setNegativeButton("取消", null).show();
    }

    private LinearLayout makeVaultRow(final String name, final String kind) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, dp(7), 0, dp(7));
        TextView t = new TextView(this);
        t.setText(name);
        t.setTextSize(13.5f);
        t.setTextColor(theme.text());
        t.setLayoutParams(new LinearLayout.LayoutParams(0, -2, 1f));
        row.addView(t);
        final TextView v = new TextView(this);
        v.setText(String.valueOf("limit".equals(kind) ? AccountVault.sendLimit(this) : AccountVault.cooldownMin(this)));
        v.setTextSize(13.5f);
        v.setTextColor(theme.accent());
        row.addView(v);
        row.setOnClickListener(x -> {
            final EditText et = new EditText(this);
            et.setInputType(InputType.TYPE_CLASS_NUMBER);
            et.setText(v.getText().toString());
            et.setTextColor(theme.text());
            new AlertDialog.Builder(this).setTitle("修改 " + name).setView(et)
                    .setPositiveButton("保存", (d, w) -> {
                        try {
                            int n = Integer.parseInt(et.getText().toString().trim());
                            if ("limit".equals(kind)) AccountVault.setSendLimit(MainActivity.this, n);
                            else AccountVault.setCooldownMin(MainActivity.this, n);
                            v.setText(String.valueOf("limit".equals(kind)
                                    ? AccountVault.sendLimit(MainActivity.this)
                                    : AccountVault.cooldownMin(MainActivity.this)));
                            accountSig = "";
                            refreshAccountBar();
                        } catch (Exception ignored) {}
                    }).setNegativeButton("取消", null).show();
        });
        return row;
    }

    private void toast(String s) { Toast.makeText(this, s, Toast.LENGTH_SHORT).show(); }

    /** 小工具：让设置读写也能走 Util.prefs 的包装。 */
    private static class SharedPreferencesHelper {
        private final android.content.Context ctx;
        SharedPreferencesHelper(android.content.Context c) { this.ctx = c; }
    }
}
