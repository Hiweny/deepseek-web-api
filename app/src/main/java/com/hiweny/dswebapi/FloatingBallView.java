package com.hiweny.dswebapi;

import android.animation.ObjectAnimator;
import android.animation.PropertyValuesHolder;
import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.RadialGradient;
import android.graphics.Shader;
import android.os.Build;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;
import android.view.WindowManager;

/**
 * 悬浮球（原生自绘的「✨ 星」）：<b>静态</b>渲染——无消息时完全静止、不重绘、不动画；
 * AI 有请求进行中时仅做「轻微呼吸」（View 属性动画，不触发 onDraw，开销极低）。
 *
 * <p>性能要点（避免影响 WebView 与输入）：
 * <ul>
 *   <li>星星预渲染成一张 Bitmap，onDraw 只做一次 drawBitmap —— 不做每帧渐变对象分配</li>
 *   <li>无任何常驻动画；呼吸只在 busy 时用 alpha/scale 属性动画（硬件层处理）</li>
 *   <li>窗口置 {@code FLAG_NOT_TOUCH_MODAL}（球外触摸穿透给下方窗口）+ {@code FLAG_NOT_FOCUSABLE}（不抢焦点）</li>
 * </ul>
 * 拖动松手后直接吸附到屏幕左右边缘。
 */
public class FloatingBallView extends View {

    public interface Listener { void onTap(); }

    private static final int WIN_DP = 60;      // 窗口边长（含光晕）
    private static final int EDGE_DP = 0;      // 贴边留白（0 = 完全贴边）

    private final float density;
    private final int winSize;
    private final int touchSlop;
    private final SharedPreferences prefs;
    private final WindowManager wm;
    private final WindowManager.LayoutParams lp;
    private final Listener listener;

    private Bitmap sprite;          // 预渲染的星星（含光晕）
    private ObjectAnimator breath;  // 仅 busy 时存在
    private boolean busy = false;

    private float downRawX, downRawY;
    private int downLpX, downLpY;
    private boolean dragging = false;
    private boolean moved = false;

    public FloatingBallView(Context ctx, Listener listener) {
        super(ctx);
        this.listener = listener;
        this.density = ctx.getResources().getDisplayMetrics().density;
        this.winSize = (int) (WIN_DP * density);
        this.touchSlop = ViewConfiguration.get(ctx).getScaledTouchSlop();
        this.prefs = ctx.getSharedPreferences("dswebapi", Context.MODE_PRIVATE);
        this.wm = (WindowManager) ctx.getSystemService(Context.WINDOW_SERVICE);

        int type = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
                : WindowManager.LayoutParams.TYPE_PHONE;
        lp = new WindowManager.LayoutParams(winSize, winSize, type,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE        // 不抢输入焦点
                        | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL  // ★ 球外触摸事件穿透给下层
                        | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS
                        | WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
                PixelFormat.TRANSLUCENT);
        lp.gravity = Gravity.TOP | Gravity.START;
        int defX = ctx.getResources().getDisplayMetrics().widthPixels - winSize - (int) (EDGE_DP * density);
        lp.x = prefs.getInt("ball_x", defX);
        lp.y = prefs.getInt("ball_y", (int) (200 * density));
        setPivotX(winSize / 2f);
        setPivotY(winSize / 2f);
    }

    public WindowManager.LayoutParams params() { return lp; }

    /** 「AI 正在回复」状态：仅切换轻微呼吸，不动其它。 */
    public void setBusy(boolean b) {
        if (busy == b) return;
        busy = b;
        applyBreath();
    }

    private void applyBreath() {
        if (breath != null) { breath.cancel(); breath = null; }
        if (!busy) {
            // 静止：属性归位，且不再有任何动画/重绘
            setAlpha(1f); setScaleX(1f); setScaleY(1f);
            return;
        }
        breath = ObjectAnimator.ofPropertyValuesHolder(this,
                PropertyValuesHolder.ofFloat("alpha", 0.84f, 1f),
                PropertyValuesHolder.ofFloat("scaleX", 0.965f, 1.035f),
                PropertyValuesHolder.ofFloat("scaleY", 0.965f, 1.035f));
        breath.setDuration(1400L);
        breath.setRepeatMode(ObjectAnimator.REVERSE);
        breath.setRepeatCount(ObjectAnimator.INFINITE);
        breath.start();
    }

    @Override
    protected void onSizeChanged(int w, int h, int oldw, int oldh) {
        super.onSizeChanged(w, h, oldw, oldh);
        buildSprite(w, h);
    }

    @Override
    protected void onDetachedFromWindow() {
        if (breath != null) { breath.cancel(); breath = null; }
        super.onDetachedFromWindow();
    }

    /* ================= 绘制（一次性预渲染成位图） ================= */

    private void buildSprite(int w, int h) {
        if (w <= 0 || h <= 0) return;
        Bitmap bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888);
        Canvas c = new Canvas(bmp);
        float cx = w / 2f, cy = h / 2f;
        float min = Math.min(w, h);
        Paint p = new Paint(Paint.ANTI_ALIAS_FLAG);

        // 1) 柔光晕（径向）
        RadialGradient glow = new RadialGradient(cx, cy, min * 0.5f,
                new int[]{ 0x66FFD86B, 0x26FFC24B, 0x00FFC24B },
                new float[]{ 0f, 0.5f, 1f }, Shader.TileMode.CLAMP);
        p.setShader(glow);
        p.setStyle(Paint.Style.FILL);
        c.drawCircle(cx, cy, min * 0.5f, p);

        // 2) 主星（四角 sparkle，金白渐变）
        float R = min * 0.30f;
        Path star = sparkle(cx, cy, R, 0.16f);
        LinearGradient lg = new LinearGradient(cx - R, cy - R, cx + R, cy + R,
                new int[]{ 0xFFFFFFFF, 0xFFFFE9A8, 0xFFFFC24B },
                new float[]{ 0f, 0.45f, 1f }, Shader.TileMode.CLAMP);
        p.setShader(lg);
        c.drawPath(star, p);

        // 3) 内层白色高光星（更小、更尖）
        p.setShader(null);
        p.setColor(0xE6FFFFFF);
        c.drawPath(sparkle(cx, cy, R * 0.52f, 0.24f), p);

        // 4) 两个小星点（右上 / 左下），营造「✨」的灵动感
        p.setColor(0xCCFFFFFF);
        c.drawPath(sparkle(cx + R * 1.20f, cy - R * 1.05f, R * 0.20f, 0.20f), p);
        p.setColor(0x99FFFFFF);
        c.drawPath(sparkle(cx - R * 1.14f, cy + R * 0.98f, R * 0.14f, 0.20f), p);

        sprite = bmp;
        invalidate();
    }

    /** 四角星路径：四尖 + 内凹（k 越小越尖）。 */
    private static Path sparkle(float cx, float cy, float r, float k) {
        float q = r * k;
        Path p = new Path();
        p.moveTo(cx, cy - r);
        p.quadTo(cx + q, cy - q, cx + r, cy);
        p.quadTo(cx + q, cy + q, cx, cy + r);
        p.quadTo(cx - q, cy + q, cx - r, cy);
        p.quadTo(cx - q, cy - q, cx, cy - r);
        p.close();
        return p;
    }

    @Override
    protected void onDraw(Canvas canvas) {
        if (sprite == null) return;      // 未就绪则什么都不画（避免空转）
        canvas.drawBitmap(sprite, 0f, 0f, null);
    }

    /* ================= 触摸：拖动 + 点击 + 贴边 ================= */

    @Override
    public boolean onTouchEvent(MotionEvent e) {
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                downRawX = e.getRawX(); downRawY = e.getRawY();
                downLpX = lp.x; downLpY = lp.y;
                dragging = true; moved = false;
                return true;
            case MotionEvent.ACTION_MOVE:
                if (!dragging) return true;
                float dx = e.getRawX() - downRawX, dy = e.getRawY() - downRawY;
                if (!moved && (Math.abs(dx) > touchSlop || Math.abs(dy) > touchSlop)) moved = true;
                if (moved) {
                    lp.x = (int) (downLpX + dx);
                    lp.y = (int) (downLpY + dy);
                    clampSelf();
                    try { wm.updateViewLayout(this, lp); } catch (Exception ignored) {}
                }
                return true;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                dragging = false;
                if (moved) {
                    snapToEdge();
                    prefs.edit().putInt("ball_x", lp.x).putInt("ball_y", lp.y).apply();
                } else if (listener != null) {
                    listener.onTap();
                }
                return true;
            default:
                return super.onTouchEvent(e);
        }
    }

    private int screenW() { return getResources().getDisplayMetrics().widthPixels; }
    private int screenH() { return getResources().getDisplayMetrics().heightPixels; }

    private void clampSelf() {
        int sw = screenW(), sh = screenH();
        if (lp.x < 0) lp.x = 0;
        if (lp.y < 0) lp.y = 0;
        if (lp.x > sw - winSize) lp.x = sw - winSize;
        if (lp.y > sh - winSize) lp.y = sh - winSize;
    }

    /** 直接吸附到左右边缘（不做动画，最可靠）。 */
    private void snapToEdge() {
        int edge = (int) (EDGE_DP * density);
        int centerX = lp.x + winSize / 2;
        lp.x = centerX < screenW() / 2 ? edge : screenW() - winSize - edge;
        clampSelf();
        try { wm.updateViewLayout(this, lp); } catch (Exception ignored) {}
        invalidate();
    }
}
