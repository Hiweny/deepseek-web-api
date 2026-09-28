package com.hiweny.dswebapi;

import android.content.Context;

/** 极简配色：深/浅两套，全部用整型 ARGB。 */
final class Theme {
    final boolean dark;

    private Theme(boolean dark) { this.dark = dark; }

    static Theme of(Context ctx) {
        return new Theme((ctx.getResources().getConfiguration().uiMode & 48) == 32);
    }

    int bg() { return dark ? 0xFF0E1116 : 0xFFF4F6FB; }
    int headerBg() { return dark ? 0xFF161A21 : 0xFFFFFFFF; }
    int cardBg() { return dark ? 0xFF1A1F27 : 0xFFFFFFFF; }
    int text() { return dark ? 0xFFE8EAED : 0xFF1A1B1F; }
    int textSub() { return dark ? 0xFF9AA3AD : 0xFF6B7280; }
    int accent() { return dark ? 0xFF6D8CFF : 0xFF3D5AFE; }
    int accentSoft() { return dark ? 0x336D8CFF : 0x193D5AFE; }
    int divider() { return dark ? 0xFF2A3038 : 0xFFE5E8EF; }
    int btnBg() { return dark ? 0xFF232A33 : 0xFFFFFFFF; }
    int btnStroke() { return dark ? 0xFF323A45 : 0xFFD8DDE6; }
    int green() { return dark ? 0xFF4ADE80 : 0xFF16A34A; }
    int red() { return dark ? 0xFFF87171 : 0xFFDC2626; }
    int amber() { return 0xFFF59E0B; }
}
