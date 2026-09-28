package com.hiweny.dswebapi;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.AccessibilityServiceInfo;
import android.view.accessibility.AccessibilityEvent;

/**
 * 无障碍保活服务：由系统绑定（普通应用无法解绑），进程优先级高、很难被系统回收。
 * 监听窗口变化并周期性自检：只要用户已启用桥服务，一旦发现服务停止（或 HTTP 服务卡死），
 * 立即重新拉起并续期心跳，从而在后台长时间放置后也能持续提供 API。
 */
public class KeepAliveAccessibility extends AccessibilityService {
    private long lastCheck = 0L;

    @Override
    protected void onServiceConnected() {
        super.onServiceConnected();
        AccessibilityServiceInfo info = new AccessibilityServiceInfo();
        info.eventTypes = AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED | AccessibilityEvent.TYPE_WINDOWS_CHANGED;
        info.feedbackType = AccessibilityServiceInfo.FEEDBACK_GENERIC;
        info.flags = AccessibilityServiceInfo.DEFAULT;
        info.notificationTimeout = 300;
        setServiceInfo(info);
        ensureService();
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
        long now = System.currentTimeMillis();
        if (now - lastCheck < 15000L) return;
        lastCheck = now;
        ensureService();
    }

    private void ensureService() {
        try {
            if (!Util.prefs(this).getBoolean("service_enabled", false)) return;
            boolean needStart = !ApiService.serviceRunning;
            if (!needStart && ApiService.lastPingOk > 0
                    && System.currentTimeMillis() - ApiService.lastPingOk > 180000L) {
                needStart = true; // HTTP 服务疑似卡死（3 分钟无心跳）
            }
            if (needStart) {
                Util.log("无障碍保活：检测到服务停止/卡死，重新拉起");
                ApiService.start(this);
            }
            KeepAlive.scheduleHeartbeat(this);
        } catch (Exception e) {
            Util.log("无障碍保活异常: " + e.getMessage());
        }
    }

    @Override
    public void onInterrupt() {}
}
