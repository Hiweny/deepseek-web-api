package com.hiweny.dswebapi;

import android.animation.ValueAnimator;
import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.PixelFormat;
import android.graphics.RadialGradient;
import android.graphics.RectF;
import android.graphics.Shader;
import android.os.Build;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;
import android.view.WindowManager;
import android.view.animation.LinearInterpolator;

/**
 * 悬浮球（原生自绘）：Siri 风格的动感光球。
 *
 * <p>状态：
 * <ul>
 *   <li>待机（idle）：柔和呼吸的光晕 + 缓慢旋转的彩色弧</li>
 *   <li>忙（busy，AI 正在回复）：脉动更快、外扩散波纹、弧光加速</li>
 * </ul>
 * 可拖动，松手自动吸附到屏幕左右边缘；单击回调 {@link Listener#onTap()}。
 */
public class FloatingBallView extends View {

    public interface Listener { void onTap(); }

    // Siri 四色：青、蓝、紫、粉
    private static final int[] PALETTE = { 0xFF00D4FF, 0xFF0A84FF, 0xFFBF5AF2, 0xFFFF375F };

    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF arcRect = new RectF();
    private final float density;
    private final int ballSize;
    private final int touchSlop;

    private ValueAnimator anim;
    private float phase = 0f;          // 0..1 循环
    private boolean busy = false;

    private final WindowManager wm;
    private final WindowManager.LayoutParams lp;
    private final SharedPreferences prefs;
    private final Listener listener;

    private float downRawX, downRawY;
    private int downLpX, downLpY;
    private boolean dragging = false;
    private boolean movedBeyondSlop = false;

    public FloatingBallView(Context ctx, Listener listener) {
        super(ctx);
        this.listener = listener;
        this.density = ctx.getResources().getDisplayMetrics().density;
        this.ballSize = (int) (56 * density);
        this.touchSlop = ViewConfiguration.get(ctx).getScaledTouchSlop();
        this.prefs = ctx.getSharedPreferences("dswebapi", Context.MODE_PRIVATE);
        this.wm = (WindowManager) ctx.getSystemService(Context.WINDOW_SERVICE);

        int type = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
                : WindowManager.LayoutParams.TYPE_PHONE;
        lp = new WindowManager.LayoutParams(ballSize, ballSize, type,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                        | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS
                        | WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
                PixelFormat.TRANSLUCENT);
        lp.gravity = Gravity.TOP | Gravity.START;
        int defX = ctx.getResources().getDisplayMetrics().widthPixels - ballSize - (int) (8 * density);
        lp.x = prefs.getInt("ball_x", defX);
        lp.y = prefs.getInt("ball_y", (int) (220 * density));
    }

    public WindowManager.LayoutParams params() { return lp; }

    /** 设置「AI 正在回复」状态（改变动效节奏与亮度）。 */
    public void setBusy(boolean b) {
        if (busy == b) return;
        busy = b;
        startAnim();
    }

    @Override
    protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        startAnim();
    }

    @Override
    protected void onDetachedFromWindow() {
        stopAnim();
        super.onDetachedFromWindow();
    }

    private void startAnim() {
        stopAnim();
        anim = ValueAnimator.ofFloat(0f, 1f);
        anim.setDuration(busy ? 900L : 1900L);
        anim.setRepeatCount(ValueAnimator.INFINITE);
        anim.setInterpolator(new LinearInterpolator());
        anim.addUpdateListener(new ValueAnimator.AnimatorUpdateListener() {
            @Override public void onAnimationUpdate(ValueAnimator a) {
                phase = (Float) a.getAnimatedValue();
                invalidate();
            }
        });
        anim.start();
    }

    private void stopAnim() {
        if (anim != null) { anim.cancel(); anim = null; }
    }

    private static float cubic(float p, int i0, int i1) {
        float f = Math.max(0f, Math.min(1f, p));
        int c0 = PALETTE[i0], c1 = PALETTE[i1];
        int r = (int) (Color.red(c0) + (Color.red(c1) - Color.red(c0)) * f);
        int g = (int) (Color.green(c0) + (Color.green(c1) - Color.green(c0)) * f);
        int b = (int) (Color.blue(c0) + (Color.blue(c1) - Color.blue(c0)) * f);
        return Color.rgb(r, g, b);
    }

    /** 随相位在四色间循环取色。 */
    private int hueColor(float p) {
        float f = (p % 1f + 1f) % 1f * PALETTE.length;
        int i = (int) f;
        return (int) cubic(f - i, i % PALETTE.length, (i + 1) % PALETTE.length);
    }

    @Override
    protected void onDraw(Canvas canvas) {
        float w = getWidth(), h = getHeight();
        float cx = w / 2f, cy = h / 2f;
        float min = Math.min(w, h);
        float baseR = min * 0.30f;

        // 呼吸（busy 更快更明显）
        float freq = busy ? 3f : 1.6f;
        float amp = busy ? 0.12f : 0.06f;
        float breath = 1f + amp * (float) Math.sin(phase * 2 * Math.PI * freq);
        float r = baseR * breath;

        int cA = hueColor(phase);
        int cB = hueColor(phase + 0.5f);

        // 1) 外发光光晕
        RadialGradient glow = new RadialGradient(cx, cy, min * 0.5f,
                new int[]{ withAlpha(cA, busy ? 130 : 90), withAlpha(cA, 0) },
                new float[]{ 0.30f, 1f }, Shader.TileMode.CLAMP);
        paint.setShader(glow);
        paint.setStyle(Paint.Style.FILL);
        canvas.drawCircle(cx, cy, min * 0.5f, paint);

        // 2) 球体：左上高光 → 主色 → 暗色
        RadialGradient body = new RadialGradient(cx - r * 0.35f, cy - r * 0.38f, r * 1.7f,
                new int[]{ lighten(cA), cA, darken(cB) },
                new float[]{ 0f, 0.55f, 1f }, Shader.TileMode.CLAMP);
        paint.setShader(body);
        canvas.drawCircle(cx, cy, r, paint);

        // 3) 内圈高光圈
        paint.setShader(null);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(Math.max(1f, min * 0.012f));
        paint.setColor(withAlpha(0xFFFFFFFF, 90));
        canvas.drawCircle(cx, cy, r * 0.82f, paint);

        // 4) 旋转弧（外环，busy 时双倍速 + 更亮）
        float speed = busy ? 2f : 1f;
        float start = (phase * 360f * speed) % 360f;
        paint.setStrokeCap(Paint.Cap.ROUND);
        paint.setStrokeWidth(Math.max(2f, min * 0.05f));
        float ar = r * 1.28f;
        arcRect.set(cx - ar, cy - ar, cx + ar, cy + ar);
        paint.setColor(withAlpha(cB, busy ? 235 : 170));
        canvas.drawArc(arcRect, start, busy ? 120f : 90f, false, paint);
        paint.setColor(withAlpha(cA, busy ? 200 : 130));
        canvas.drawArc(arcRect, start + 180f, busy ? 80f : 60f, false, paint);

        // 5) busy：向外扩散的波纹
        if (busy) {
            for (int k = 0; k < 2; k++) {
                float rp = (phase + k * 0.5f) % 1f;
                float rr = r * 1.2f + rp * min * 0.28f;
                int alpha = (int) ((1f - rp) * 95);
                paint.setStyle(Paint.Style.STROKE);
                paint.setStrokeWidth(Math.max(1.5f, min * 0.02f));
                paint.setColor(withAlpha(k == 0 ? cA : cB, alpha));
                canvas.drawCircle(cx, cy, rr, paint);
            }
        }
    }

    private static int withAlpha(int color, int a) {
        return (color & 0x00FFFFFF) | ((Math.max(0, Math.min(255, a)) & 0xFF) << 24);
    }
    private static int lighten(int color) {
        return Color.rgb(Math.min(255, Color.red(color) + 90),
                Math.min(255, Color.green(color) + 90), Math.min(255, Color.blue(color) + 90));
    }
    private static int darken(int color) {
        return Color.rgb((int) (Color.red(color) * 0.45f), (int) (Color.green(color) * 0.45f), (int) (Color.blue(color) * 0.6f));
    }

    private boolean dragMoved = false;

    @Override
    public boolean onTouchEvent(MotionEvent e) {
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                downRawX = e.getRawX(); downRawY = e.getRawY();
                downLpX = lp.x; downLpY = lp.y;
                dragging = true; movedBeyondSlop = false; dragMoved = false;
                return true;
            case MotionEvent.ACTION_MOVE:
                if (!dragging) return true;
                float dx = e.getRawX() - downRawX, dy = e.getRawY() - downRawY;
                if (!movedBeyondSlop && (Math.abs(dx) > touchSlop || Math.abs(dy) > touchSlop)) movedBeyondSlop = true;
                if (movedBeyondSlop) {
                    lp.x = (int) (downLpX + dx);
                    lp.y = (int) (downLpY + dy);
                    clampAndUpdate();
                    dragMoved = true;
                    invalidate();
                }
                return true;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                dragging = false;
                if (dragMoved) {
                    snapToEdge();
                    prefs.edit().putInt("ball_x", lp.x).putInt("ball_y", lp.y).apply();
                } else {
                    if (listener != null) listener.onTap();
                }
                return true;
            default:
                return super.onTouchEvent(e);
        }
    }

    private void clampAndUpdate() {
        int screenW = getResources().getDisplayMetrics().widthPixels;
        int screenH = getResources().getDisplayMetrics().heightPixels;
        if (lp.x < 0) lp.x = 0;
        if (lp.y < 0) lp.y = 0;
        if (lp.x > screenW - ballSize) lp.x = screenW - ballSize;
        if (lp.y > screenH - ballSize) lp.y = screenH - ballSize;
        try { wm.updateViewLayout(this, lp); } catch (Exception ignored) {}
    }

    private void snapToEdge() {
        int screenW = getResources().getDisplayMetrics().widthPixels;
        int mid = lp.x + ballSize / 2;
        int target = mid < screenW / 2 ? (int) (6 * density) : screenW - ballSize - (int) (6 * density);
        animateToX(target);
    }

    private void animateToX(final int target) {
        final int from = lp.x;
        ValueAnimator a = ValueAnimator.ofFloat(0f, 1f);
        a.setDuration(180L);
        a.setInterpolator(new android.view.animation.DecelerateInterpolator());
        a.addUpdateListener(new ValueAnimator.AnimatorUpdateListener() {
            @Override public void onAnimationUpdate(ValueAnimator va) {
                float f = (Float) va.getAnimatedValue();
                lp.x = (int) (from + (target - from) * f);
                try { wm.updateViewLayout(FloatingBallView.this, lp); } catch (Exception ignored) {}
            }
        });
        a.start();
    }
}
