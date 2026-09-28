package com.hiweny.dswebapi;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;

/** 下载 http(s) 图片/文件为附件（供外部请求中的图片 URL 使用）。 */
public final class Downloader {
    private static final int MAX_BYTES = 25 * 1024 * 1024;

    private Downloader() {}

    public static PromptBuilder.Attachment fetch(String url, String nameHint) {
        HttpURLConnection conn = null;
        try {
            conn = (HttpURLConnection) new URL(url).openConnection();
            conn.setConnectTimeout(15000);
            conn.setReadTimeout(30000);
            conn.setInstanceFollowRedirects(true);
            conn.setRequestProperty("User-Agent", "Mozilla/5.0");
            int code = conn.getResponseCode();
            if (code < 200 || code >= 300) return null;
            String mime = conn.getContentType();
            if (mime == null || mime.isEmpty()) mime = "application/octet-stream";
            int semi = mime.indexOf(';');
            if (semi >= 0) mime = mime.substring(0, semi).trim();
            InputStream in = conn.getInputStream();
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            byte[] buf = new byte[16384];
            int n, total = 0;
            while ((n = in.read(buf)) > 0) {
                total += n;
                if (total > MAX_BYTES) { in.close(); return null; }
                bos.write(buf, 0, n);
            }
            in.close();
            byte[] data = bos.toByteArray();
            if (data.length == 0) return null;
            String ext = extFor(mime, url);
            String name = (nameHint == null || nameHint.isEmpty()) ? ("file-" + System.currentTimeMillis()) : nameHint;
            if (!name.contains(".")) name = name + ext;
            return new PromptBuilder.Attachment(name, mime, Util.b64(data));
        } catch (Exception e) {
            Util.log("下载失败 " + url + " : " + e.getMessage());
            return null;
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    private static String extFor(String mime, String url) {
        if (mime.contains("png")) return ".png";
        if (mime.contains("jpeg") || mime.contains("jpg")) return ".jpg";
        if (mime.contains("gif")) return ".gif";
        if (mime.contains("webp")) return ".webp";
        if (mime.contains("pdf")) return ".pdf";
        if (mime.contains("text")) return ".txt";
        int q = url.indexOf('?');
        String path = q >= 0 ? url.substring(0, q) : url;
        int dot = path.lastIndexOf('.');
        int slash = path.lastIndexOf('/');
        if (dot > slash && dot >= 0) return path.substring(dot);
        return "";
    }
}
