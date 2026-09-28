# 保留 JS 桥接口，避免被混淆（当前 minifyEnabled=false，仅作保险）
-keepclassmembers class * {
    @android.webkit.JavascriptInterface <methods>;
}
-keep class com.hiweny.dswebapi.** { *; }
