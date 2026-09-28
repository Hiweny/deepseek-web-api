package com.hiweny.dswebapi;

import java.io.BufferedInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** 极简本地 HTTP/1.1 服务器（仅监听 127.0.0.1），支持普通响应与 SSE 分块流式响应。 */
public class HttpBridgeServer {

    public interface Router { void handle(Request req, Response res) throws Exception; }

    private final int port;
    private final Router router;
    private ServerSocket server;
    private volatile boolean running;
    private final ExecutorService pool = Executors.newCachedThreadPool();

    public HttpBridgeServer(int port, Router router) { this.port = port; this.router = router; }

    public void start() throws IOException {
        server = new ServerSocket();
        server.setReuseAddress(true);
        server.bind(new InetSocketAddress("127.0.0.1", port), 128);
        running = true;
        Thread t = new Thread(this::acceptLoop, "http-accept");
        t.setDaemon(true);
        t.start();
        Util.log("HTTP 服务已启动: http://127.0.0.1:" + port);
    }

    public void stop() {
        running = false;
        try { if (server != null) server.close(); } catch (Exception ignored) {}
        pool.shutdownNow();
    }

    private void acceptLoop() {
        while (running) {
            try {
                Socket s = server.accept();
                pool.execute(() -> handleConn(s));
            } catch (Exception e) {
                if (running) Util.log("accept 异常: " + e.getMessage());
            }
        }
    }

    private void handleConn(Socket socket) {
        Response res = null;
        try {
            socket.setTcpNoDelay(true);
            socket.setSoTimeout(600000);
            BufferedInputStream in = new BufferedInputStream(socket.getInputStream());
            OutputStream out = socket.getOutputStream();

            String requestLine = readLine(in);
            if (requestLine == null || requestLine.isEmpty()) { socket.close(); return; }
            String[] parts = requestLine.split(" ");
            if (parts.length < 2) { socket.close(); return; }
            String method = parts[0].toUpperCase(Locale.ROOT);
            String fullPath = parts[1];

            Map<String, String> headers = new LinkedHashMap<>();
            String line;
            while ((line = readLine(in)) != null && !line.isEmpty()) {
                int c = line.indexOf(':');
                if (c > 0) headers.put(line.substring(0, c).trim().toLowerCase(Locale.ROOT), line.substring(c + 1).trim());
            }

            String body = readBody(in, headers);

            String path = fullPath;
            String query = "";
            int q = fullPath.indexOf('?');
            if (q >= 0) { path = fullPath.substring(0, q); query = fullPath.substring(q + 1); }

            Request req = new Request(method, path, query, headers, body);
            res = new Response(socket, out, "HEAD".equals(method));

            if ("OPTIONS".equals(method)) {
                res.setHeader("Access-Control-Allow-Origin", "*");
                res.setHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                res.setHeader("Access-Control-Allow-Headers", "Authorization, Content-Type, *");
                res.sendStatus(204);
                res.close();
                return;
            }
            router.handle(req, res);
        } catch (Exception e) {
            try { if (res != null) res.close(); } catch (Exception ignored) {}
        }
    }

    private static String readLine(InputStream in) throws IOException {
        StringBuilder sb = new StringBuilder();
        int c;
        while ((c = in.read()) != -1) {
            if (c == '\n') break;
            if (c == '\r') continue;
            sb.append((char) c);
            if (sb.length() > 65536) break;
        }
        if (c == -1 && sb.length() == 0) return null;
        return sb.toString();
    }

    private static String readBody(InputStream in, Map<String, String> headers) throws IOException {
        String te = headers.get("transfer-encoding");
        if (te != null && te.toLowerCase(Locale.ROOT).contains("chunked")) {
            StringBuilder sb = new StringBuilder();
            while (true) {
                String sizeLine = readLine(in);
                if (sizeLine == null) break;
                int semi = sizeLine.indexOf(';');
                if (semi >= 0) sizeLine = sizeLine.substring(0, semi);
                sizeLine = sizeLine.trim();
                if (sizeLine.isEmpty()) continue;
                int size;
                try { size = Integer.parseInt(sizeLine, 16); } catch (Exception e) { break; }
                if (size == 0) { readLine(in); break; }
                byte[] buf = new byte[size];
                int read = 0;
                while (read < size) {
                    int r = in.read(buf, read, size - read);
                    if (r < 0) break;
                    read += r;
                }
                sb.append(new String(buf, 0, read, StandardCharsets.UTF_8));
                readLine(in);
            }
            return sb.toString();
        }
        String cl = headers.get("content-length");
        if (cl == null) return "";
        int len;
        try { len = Integer.parseInt(cl.trim()); } catch (Exception e) { return ""; }
        if (len <= 0) return "";
        if (len > 64 * 1024 * 1024) len = 64 * 1024 * 1024;
        byte[] buf = new byte[len];
        int read = 0;
        while (read < len) {
            int r = in.read(buf, read, len - read);
            if (r < 0) break;
            read += r;
        }
        return new String(buf, 0, read, StandardCharsets.UTF_8);
    }

    /* ================= 请求 / 响应 ================= */

    public static class Request {
        public final String method, path, query, body;
        public final Map<String, String> headers;
        Request(String method, String path, String query, Map<String, String> headers, String body) {
            this.method = method; this.path = path; this.query = query; this.headers = headers; this.body = body;
        }
        public String header(String name) { return headers.get(name.toLowerCase(Locale.ROOT)); }
        public String bearer() {
            String a = header("authorization");
            if (a == null) return "";
            a = a.trim();
            if (a.toLowerCase(Locale.ROOT).startsWith("bearer ")) return a.substring(7).trim();
            return a;
        }
    }

    public static class Response {
        private final Socket socket;
        private final OutputStream out;
        private final boolean headOnly;
        private boolean started = false, chunked = false, closed = false;
        private int status = 200;
        private String statusText = "OK";
        private final Map<String, String> headers = new LinkedHashMap<>();

        Response(Socket socket, OutputStream out, boolean headOnly) {
            this.socket = socket; this.out = out; this.headOnly = headOnly;
        }

        public void setHeader(String k, String v) { headers.put(k, v); }
        public void setStatus(int code, String text) { status = code; statusText = text; }
        public boolean isClosed() { return closed; }

        private void writeHead() throws IOException {
            if (started) return;
            started = true;
            StringBuilder sb = new StringBuilder();
            sb.append("HTTP/1.1 ").append(status).append(' ').append(statusText).append("\r\n");
            if (!headers.containsKey("Access-Control-Allow-Origin")) headers.put("Access-Control-Allow-Origin", "*");
            for (Map.Entry<String, String> e : headers.entrySet()) {
                sb.append(e.getKey()).append(": ").append(e.getValue()).append("\r\n");
            }
            sb.append("Connection: close\r\n\r\n");
            out.write(sb.toString().getBytes(StandardCharsets.UTF_8));
        }

        public void sendJson(int status, String body) throws IOException {
            if (closed) return;
            setStatus(status, status == 200 ? "OK" : "Error");
            byte[] b = body == null ? new byte[0] : body.getBytes(StandardCharsets.UTF_8);
            headers.put("Content-Type", "application/json; charset=utf-8");
            headers.put("Content-Length", String.valueOf(b.length));
            writeHead();
            if (!headOnly) out.write(b);
            out.flush();
        }

        public void sendJson(String body) throws IOException { sendJson(200, body); }

        public void sendStatus(int code) throws IOException {
            setStatus(code, code == 204 ? "No Content" : "OK");
            headers.put("Content-Length", "0");
            writeHead();
            out.flush();
        }

        /** 开启 SSE 分块流式响应。 */
        public void startSse() throws IOException {
            headers.put("Content-Type", "text/event-stream; charset=utf-8");
            headers.put("Cache-Control", "no-cache");
            headers.put("Transfer-Encoding", "chunked");
            headers.put("X-Accel-Buffering", "no");
            writeHead();
            chunked = true;
            out.flush();
        }

        public void sseData(String data) throws IOException {
            if (!chunked) startSse();
            byte[] b = ("data: " + data + "\n\n").getBytes(StandardCharsets.UTF_8);
            writeChunk(b);
        }

        public void sseComment(String comment) throws IOException {
            if (!chunked) startSse();
            writeChunk((": " + comment + "\n\n").getBytes(StandardCharsets.UTF_8));
        }

        private void writeChunk(byte[] b) throws IOException {
            out.write((Integer.toHexString(b.length) + "\r\n").getBytes(StandardCharsets.UTF_8));
            out.write(b);
            out.write("\r\n".getBytes(StandardCharsets.UTF_8));
            out.flush();
        }

        public void endChunked() throws IOException {
            if (!chunked || closed) return;
            out.write("0\r\n\r\n".getBytes(StandardCharsets.UTF_8));
            out.flush();
        }

        public void close() {
            if (closed) return;
            closed = true;
            try { if (chunked && started) endChunked(); } catch (Exception ignored) {}
            try { socket.close(); } catch (Exception ignored) {}
        }
    }
}
