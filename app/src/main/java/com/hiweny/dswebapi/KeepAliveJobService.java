package com.hiweny.dswebapi;

import android.app.job.JobInfo;
import android.app.job.JobParameters;
import android.app.job.JobScheduler;
import android.app.job.JobService;
import android.content.ComponentName;
import android.content.Context;
import android.os.Build;

/**
 * 周期性拉活（JobScheduler）：比 AlarmManager 更被系统容忍，用于服务被杀后尽快恢复。
 * 与无障碍保活、AlarmManager 心跳、开机/自更新广播共同构成多路保活；本类为增量，不影响原有机制。
 */
public class KeepAliveJobService extends JobService {

    private static final int JOB_ID = 7001;
    private static final long INTERVAL_MS = 15 * 60 * 1000L;   // 15 分钟（系统允许的最小周期）

    public static void schedule(Context ctx) {
        try {
            JobScheduler js = (JobScheduler) ctx.getSystemService(Context.JOB_SCHEDULER_SERVICE);
            if (js == null) return;
            JobInfo.Builder b = new JobInfo.Builder(JOB_ID, new ComponentName(ctx, KeepAliveJobService.class))
                    .setPersisted(true)
                    .setPeriodic(INTERVAL_MS);
            if (Build.VERSION.SDK_INT >= 24) b.setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY);
            js.schedule(b.build());
        } catch (Exception e) {
            Util.log("JobScheduler 安排失败: " + e.getMessage());
        }
    }

    public static void cancel(Context ctx) {
        try {
            JobScheduler js = (JobScheduler) ctx.getSystemService(Context.JOB_SCHEDULER_SERVICE);
            if (js != null) js.cancel(JOB_ID);
        } catch (Exception ignored) {}
    }

    @Override
    public boolean onStartJob(JobParameters params) {
        try {
            if (Util.prefs(this).getBoolean("service_enabled", false) && !ApiService.serviceRunning) {
                Util.log("JobScheduler 拉活：重新启动服务");
                ApiService.start(this);
            }
        } catch (Exception ignored) {}
        return false;   // 无异步工作
    }

    @Override
    public boolean onStopJob(JobParameters params) {
        return true;    // 被中断则重排
    }
}
