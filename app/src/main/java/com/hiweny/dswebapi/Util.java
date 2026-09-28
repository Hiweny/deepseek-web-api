package com.hiweny.dswebapi;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Base64;
import android.util.Log;

import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;

/** 通用工具：内存日志、时间、md5、base64。 */
public final class Util {
    private static final String TAG = "DSWebAPI";
    private static final List<String> LOG = new ArrayList<>();
    private static final int LOG_MAX = 600;

    private Util() {}

    public static synchronized void log(String msg) {
        String ts = new SimpleDateFormat("MM-dd HH:mm:ss", Locale.CHINA).format(new Date());
        LOG.add("[" + ts + "] " + msg);
        if (LOG.size() > LOG_MAX) LOG.remove(0);
        Log.d(TAG, msg);
    }

    public static synchronized String logText() {
        StringBuilder sb = new StringBuilder();
        int from = Math.max(0, LOG.size() - 300);
        for (int i = from; i < LOG.size(); i++) sb.append(LOG.get(i)).append('\n');
        return sb.toString();
    }

    public static synchronized void clearLog() { LOG.clear(); }

    public static String timeHM(long ts) {
        if (ts <= 0) return "—";
        return new SimpleDateFormat("HH:mm:ss", Locale.CHINA).format(new Date(ts));
    }

    public static String md5(String s) {
        try {
            byte[] d = java.security.MessageDigest.getInstance("MD5").digest(s.getBytes("UTF-8"));
            StringBuilder sb = new StringBuilder();
            for (byte b : d) sb.append(String.format("%02x", b));
            return sb.toString();
        } catch (Exception e) {
            return String.valueOf(s.hashCode());
        }
    }

    public static String b64(byte[] data) {
        return Base64.encodeToString(data, Base64.NO_WRAP);
    }

    public static byte[] unb64(String s) {
        try { return Base64.decode(s, Base64.DEFAULT); } catch (Exception e) { return new byte[0]; }
    }

    public static SharedPreferences prefs(Context ctx) {
        return ctx.getSharedPreferences("dswebapi", Context.MODE_PRIVATE);
    }
}
