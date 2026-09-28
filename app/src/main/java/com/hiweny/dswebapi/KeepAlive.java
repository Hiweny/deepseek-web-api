package com.hiweny.dswebapi;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.SystemClock;

/** 后台保活：AlarmManager 心跳（Doze 下也能周期性唤醒）+ 异常/划掉后重启服务。 */
public final class KeepAlive {
    private KeepAlive() {}

    private static final long HEARTBEAT_INTERVAL = 9 * 60 * 1000L;
    public static final String ACTION_HEARTBEAT = "com.hiweny.dswebapi.HEARTBEAT";

    private static int piFlags() {
        int f = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= 23) f |= PendingIntent.FLAG_IMMUTABLE;
        return f;
    }

    private static PendingIntent heartbeatIntent(Context ctx) {
        Intent i = new Intent(ctx, SystemReceiver.class).setAction(ACTION_HEARTBEAT);
        return PendingIntent.getBroadcast(ctx, 10, i, piFlags());
    }

    public static void scheduleHeartbeat(Context ctx) {
        try {
            AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
            long at = SystemClock.elapsedRealtime() + HEARTBEAT_INTERVAL;
            am.setAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, at, heartbeatIntent(ctx));
        } catch (Exception e) {
            Util.log("心跳安排失败: " + e.getMessage());
        }
    }

    public static void cancelHeartbeat(Context ctx) {
        try {
            AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
            am.cancel(heartbeatIntent(ctx));
        } catch (Exception ignored) {}
    }

    public static void scheduleServiceRestart(Context ctx, long delayMs) {
        try {
            AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
            Intent i = new Intent(ctx, ApiService.class).setAction(ApiService.ACTION_START);
            int f = PendingIntent.FLAG_ONE_SHOT;
            if (Build.VERSION.SDK_INT >= 23) f |= PendingIntent.FLAG_IMMUTABLE;
            PendingIntent pi = PendingIntent.getService(ctx, 11, i, f);
            am.set(AlarmManager.ELAPSED_REALTIME_WAKEUP, SystemClock.elapsedRealtime() + delayMs, pi);
        } catch (Exception e) {
            Util.log("重启安排失败: " + e.getMessage());
        }
    }
}
