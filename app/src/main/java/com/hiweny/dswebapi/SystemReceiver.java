package com.hiweny.dswebapi;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** 系统事件接收器：开机自启 + AlarmManager 心跳续期/拉起服务。 */
public class SystemReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent == null ? "" : intent.getAction();
        boolean boot = Intent.ACTION_BOOT_COMPLETED.equals(action)
                || "android.intent.action.QUICKBOOT_POWERON".equals(action)
                || "com.htc.intent.action.QUICKBOOT_POWERON".equals(action);
        boolean heartbeat = KeepAlive.ACTION_HEARTBEAT.equals(action);
        if (boot || heartbeat) {
            Util.log("SystemReceiver: " + action);
            if (Util.prefs(context).getBoolean("service_enabled", false)) {
                ApiService.start(context);
            }
            KeepAlive.scheduleHeartbeat(context);
        }
    }
}
