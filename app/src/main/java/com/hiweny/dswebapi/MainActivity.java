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
    private FrameLayout webPanel;
    private View controlPanel;
    private int currentTab = TAB_CONTROL;

    private TextView tvService, tvWeb, tvLogin, tvCalls, tvLast, tvBase, tvKey, tvLog;
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
        WebHost.ensure(this, WebHost.loadBridgeJs(this));
    }

    private final Runnable attachWebRunnable = new Runnable() {
        int tries = 0;
        @Override public void run() {
            if (WebHost.get() != null) {
                if (webPanel != null) WebHost.attachTo(webPanel);
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
            WebHost.attachTo(webPanel);
            handler.post(attachWebRunnable);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (webPanel != null) WebHost.attachTo(webPanel);
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
        webPanel = new FrameLayout(this);
        webPanel.setBackgroundColor(theme.bg());
        webPanel.setVisibility(View.GONE);
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
        if (!control) WebHost.attachTo(webPanel);
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
        controlScroll.addView(col, new LinearLayout.LayoutParams(-1, -2));

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
        set.addView(makeSwitch("保持屏幕常亮", "提高后台存活率（可关）", "keep_screen"));
        col.addView(set);

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
        sw.setChecked(Util.prefs(this).getBoolean(key, "keep_screen".equals(key)));
        sw.setOnCheckedChangeListener((b, checked) -> {
            Util.prefs(this).edit().putBoolean(key, checked).apply();
            if ("keep_screen".equals(key)) {
                if (checked) getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
                else getWindow().clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
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
            case "thinking_mode": return sp.getString("thinking_mode", "auto");
            case "search_mode": return sp.getString("search_mode", "auto");
        }
        return "";
    }

    private void putSetting(String key, String value) {
        android.content.SharedPreferences.Editor e = Util.prefs(this).edit();
        if ("port".equals(key)) { try { e.putInt("port", Integer.parseInt(value)); e.remove("port_active"); } catch (Exception ignored) {} }
        else if ("timeout".equals(key)) { try { e.putInt("timeout", Integer.parseInt(value)); } catch (Exception ignored) {} }
        else e.putString(key, value);
        e.apply();
    }

    private void editSetting(final String name, final String key, final TextView view) {
        final EditText et = new EditText(this);
        et.setText(String.valueOf(currentSetting(key)));
        et.setTextColor(theme.text());
        et.setInputType("port".equals(key) || "timeout".equals(key) ? InputType.TYPE_CLASS_NUMBER : InputType.TYPE_CLASS_TEXT);
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
        }
    }

    private void doNewChat() {
        new Thread(() -> {
            JSONObject r = DeepSeekController.get().newChat();
            handler.post(() -> toast(r.optBoolean("ok") ? "已新建对话" : "新建对话失败（请在对话页手动操作）"));
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
        tvLast.setText(ApiService.lastCallText());
        tvBase.setText("http://127.0.0.1:" + currentSetting("port") + "/v1");
        tvKey.setText(String.valueOf(currentSetting("apikey")));
        if (tvLog != null) tvLog.setText(Util.logText());
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

    private void toast(String s) { Toast.makeText(this, s, Toast.LENGTH_SHORT).show(); }

    /** 小工具：让设置读写也能走 Util.prefs 的包装。 */
    private static class SharedPreferencesHelper {
        private final android.content.Context ctx;
        SharedPreferencesHelper(android.content.Context c) { this.ctx = c; }
    }
}
