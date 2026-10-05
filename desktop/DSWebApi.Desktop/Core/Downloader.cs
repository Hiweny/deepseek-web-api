namespace DSWebApi.Desktop.Core;

/// <summary>下载 http(s) 图片/文件为附件（供外部请求中的图片 URL 使用）。</summary>
public static class Downloader
{
    private const int MAX_BYTES = 25 * 1024 * 1024;
    private static readonly HttpClient Http;

    static Downloader()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
    }

    public static PromptBuilder.Attachment Fetch(string url, string nameHint)
    {
        try
        {
            using var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            string mime = resp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrEmpty(mime)) mime = "application/octet-stream";
            int semi = mime.IndexOf(';');
            if (semi >= 0) mime = mime.Substring(0, semi).Trim();

            using var stream = resp.Content.ReadAsStream();
            var ms = new MemoryStream();
            var buf = new byte[16384];
            int n, total = 0;
            while ((n = stream.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > MAX_BYTES) return null;
                ms.Write(buf, 0, n);
            }
            var data = ms.ToArray();
            if (data.Length == 0) return null;
            string ext = ExtFor(mime, url);
            string name = string.IsNullOrEmpty(nameHint) ? ("file-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : nameHint;
            if (!name.Contains('.')) name += ext;
            return new PromptBuilder.Attachment(name, mime, Convert.ToBase64String(data));
        }
        catch (Exception e)
        {
            Log.Write("下载失败 " + url + " : " + e.Message);
            return null;
        }
    }

    private static string ExtFor(string mime, string url)
    {
        if (mime.Contains("png")) return ".png";
        if (mime.Contains("jpeg") || mime.Contains("jpg")) return ".jpg";
        if (mime.Contains("gif")) return ".gif";
        if (mime.Contains("webp")) return ".webp";
        if (mime.Contains("pdf")) return ".pdf";
        if (mime.Contains("text")) return ".txt";
        int q = url.IndexOf('?');
        string path = q >= 0 ? url.Substring(0, q) : url;
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        if (dot > slash && dot >= 0) return path.Substring(dot);
        return "";
    }
}
