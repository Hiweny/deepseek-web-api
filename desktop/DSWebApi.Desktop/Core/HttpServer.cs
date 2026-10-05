using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DSWebApi.Desktop.Core;

/// <summary>
/// 极简 HTTP/1.1 服务器（基于 TcpListener，监听 0.0.0.0 支持局域网，无需管理员权限），
/// 支持普通响应与 SSE 分块流式响应。与 APK 的 HttpBridgeServer 行为对齐。
/// </summary>
public sealed class HttpServer
{
    public interface IRouter
    {
        void Handle(Request req, Response res);
    }

    public sealed class Request
    {
        public string Method = "";
        public string Path = "";
        public string Query = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body = "";
        public string RemoteAddress = "";

        public string Bearer()
        {
            if (!Headers.TryGetValue("authorization", out var h) || h == null) return "";
            h = h.Trim();
            if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return h.Substring(7).Trim();
            if (h.StartsWith("bearer", StringComparison.OrdinalIgnoreCase)) return h.Substring(6).Trim();
            return h;
        }

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";
    }

    public sealed class Response
    {
        private readonly NetworkStream _stream;
        private readonly Socket _socket;
        private readonly bool _headOnly;
        private bool _headersSent;
        private bool _chunked;
        private bool _closed;

        internal Response(Socket socket, NetworkStream stream, bool headOnly)
        {
            _socket = socket;
            _stream = stream;
            _headOnly = headOnly;
        }

        public bool Closed => _closed;

        private void WriteRaw(byte[] data)
        {
            if (_closed) return;
            if (_headOnly) return;
            try { _stream.Write(data, 0, data.Length); _stream.Flush(); }
            catch (Exception) { _closed = true; }
        }

        private void SendHeaders(int code, string reason, string contentType, long? contentLength, bool chunked, bool cors)
        {
            if (_headersSent) return;
            _headersSent = true;
            _chunked = chunked;
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            if (cors)
            {
                sb.Append("Access-Control-Allow-Origin: *\r\n");
                sb.Append("Access-Control-Allow-Headers: Authorization, Content-Type, *\r\n");
                sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            }
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            if (chunked) sb.Append("Transfer-Encoding: chunked\r\n");
            else if (contentLength.HasValue) sb.Append("Content-Length: ").Append(contentLength.Value).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
            sb.Append("X-Accel-Buffering: no\r\n");
            sb.Append("\r\n");
            WriteRaw(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public void SendJson(string json) => SendJson(200, json);

        public void SendJson(int code, string json)
        {
            var body = Encoding.UTF8.GetBytes(json ?? "{}");
            SendHeaders(code, Reason(code), "application/json; charset=utf-8", body.Length, false, true);
            WriteRaw(body);
            Close();
        }

        public void SendText(int code, string text, string contentType = "text/plain; charset=utf-8")
        {
            var body = Encoding.UTF8.GetBytes(text ?? "");
            SendHeaders(code, Reason(code), contentType, body.Length, false, true);
            WriteRaw(body);
            Close();
        }

        public void SendStatus(int code)
        {
            SendHeaders(code, Reason(code), null, 0, false, true);
            Close();
        }

        public void StartSse()
        {
            SendHeaders(200, "OK", "text/event-stream; charset=utf-8", null, true, true);
        }

        public void SseData(string data)
        {
            if (_closed) return;
            var payload = Encoding.UTF8.GetBytes("data: " + (data ?? "") + "\n\n");
            WriteChunk(payload);
        }

        public void SseComment(string comment)
        {
            if (_closed) return;
            var payload = Encoding.UTF8.GetBytes(": " + (comment ?? "") + "\n\n");
            WriteChunk(payload);
        }

        private void WriteChunk(byte[] payload)
        {
            if (!_chunked) { WriteRaw(payload); return; }
            var head = Encoding.ASCII.GetBytes(payload.Length.ToString("x") + "\r\n");
            WriteRaw(head);
            WriteRaw(payload);
            WriteRaw(Encoding.ASCII.GetBytes("\r\n"));
        }

        public void EndChunked()
        {
            if (_closed || !_chunked) return;
            WriteRaw(Encoding.ASCII.GetBytes("0\r\n\r\n"));
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try { _stream.Flush(); } catch { }
            try { _socket.Shutdown(SocketShutdown.Both); } catch { }
            try { _socket.Close(); } catch { }
        }

        private static string Reason(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 413: return "Payload Too Large";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                default: return "Status";
            }
        }
    }

    private readonly int _port;
    private readonly IRouter _router;
    private TcpListener _listener;
    private volatile bool _running;

    public int Port => _port;
    public bool Running => _running;

    public HttpServer(int port, IRouter router)
    {
        _port = port;
        _router = router;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start(128);
        _running = true;
        var t = new Thread(AcceptLoop) { IsBackground = true, Name = "http-accept" };
        t.Start();
        Log.Write("HTTP 服务已启动: http://0.0.0.0:" + _port + "/v1 （局域网可用本机 IP 访问）");
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            Socket s = null;
            try
            {
                s = _listener.AcceptSocket();
            }
            catch (Exception)
            {
                if (_running) continue;
                break;
            }
            var sock = s;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { HandleConn(sock); }
                catch (Exception e) { Log.Write("连接处理异常: " + e.Message); }
            });
        }
    }

    private void HandleConn(Socket socket)
    {
        Response res = null;
        try
        {
            socket.NoDelay = true;
            socket.ReceiveTimeout = 600000;
            socket.SendTimeout = 600000;
            var stream = new NetworkStream(socket, false);

            string requestLine = ReadLine(stream);
            if (string.IsNullOrEmpty(requestLine)) { socket.Close(); return; }
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) { socket.Close(); return; }
            string method = parts[0].ToUpperInvariant();
            string fullPath = parts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            int guard = 0;
            while (!string.IsNullOrEmpty(line = ReadLine(stream)) && guard++ < 200)
            {
                int c = line.IndexOf(':');
                if (c > 0) headers[line.Substring(0, c).Trim().ToLowerInvariant()] = line.Substring(c + 1).Trim();
            }
            if (line == null) { socket.Close(); return; }

            // Expect: 100-continue
            if (headers.TryGetValue("expect", out var exp) && exp.ToLowerInvariant().Contains("100-continue"))
            {
                var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                stream.Write(cont, 0, cont.Length);
                stream.Flush();
            }

            string body = ReadBody(stream, headers);

            string path = fullPath, query = "";
            int q = fullPath.IndexOf('?');
            if (q >= 0) { path = fullPath.Substring(0, q); query = fullPath.Substring(q + 1); }

            var req = new Request
            {
                Method = method,
                Path = path,
                Query = query,
                Headers = headers,
                Body = body,
                RemoteAddress = (socket.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "",
            };
            res = new Response(socket, stream, method == "HEAD");

            if (method == "OPTIONS")
            {
                res.SendStatus(204);
                return;
            }
            _router.Handle(req, res);
        }
        catch (Exception e)
        {
            Log.Write("请求异常: " + e.Message);
            try { if (res != null) { res.SendJson(500, "{\"error\":{\"message\":" + Json.Quote(e.Message) + ",\"type\":\"server_error\"}}"); } } catch { }
            try { if (res == null) socket.Close(); } catch { }
        }
    }

    /// <summary>返回 null 表示流已结束。</summary>
    private static string ReadLine(NetworkStream s)
    {
        var ms = new MemoryStream();
        int b;
        bool any = false;
        while ((b = s.ReadByte()) != -1)
        {
            any = true;
            if (b == '\n') break;
            if (b == '\r') continue;
            ms.WriteByte((byte)b);
            if (ms.Length > 65536) break;
        }
        if (!any && ms.Length == 0) return null;
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private const int MaxBody = 200 * 1024 * 1024;

    private static string ReadBody(NetworkStream stream, Dictionary<string, string> headers)
    {
        string te = headers.TryGetValue("transfer-encoding", out var v) ? v : "";
        if (te.ToLowerInvariant().Contains("chunked"))
        {
            var sb = new StringBuilder();
            while (true)
            {
                string sizeLine = ReadLine(stream);
                if (sizeLine == null) break;
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                sizeLine = sizeLine.Trim();
                if (sizeLine.Length == 0) continue;
                int size;
                try { size = Convert.ToInt32(sizeLine, 16); } catch { break; }
                if (size == 0) { ReadLine(stream); break; }
                var buf = new byte[size];
                int read = 0;
                while (read < size)
                {
                    int r = stream.Read(buf, read, size - read);
                    if (r <= 0) break;
                    read += r;
                }
                sb.Append(Encoding.UTF8.GetString(buf, 0, read));
                if (sb.Length > MaxBody) break;
                ReadLine(stream);
            }
            return sb.ToString();
        }

        if (!headers.TryGetValue("content-length", out var clStr) || !long.TryParse(clStr.Trim(), out var cl) || cl <= 0)
            return "";
        if (cl > MaxBody) return "";
        var data = new byte[cl];
        int total = 0;
        while (total < cl)
        {
            int r = stream.Read(data, total, (int)(cl - total));
            if (r <= 0) break;
            total += r;
        }
        return Encoding.UTF8.GetString(data, 0, total);
    }
}
