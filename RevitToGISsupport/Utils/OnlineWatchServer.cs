using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace RevitToGISsupport.Utils
{
    /// <summary>
    /// HTTP server siêu nhẹ:
    ///  - "/"        -> trả viewer.html
    ///  - "/data"    -> trả GeoJSON:
    ///                 + ưu tiên _geoJson (nếu có)
    ///                 + nếu ?file=... -> đọc file chỉ định
    ///                 + nếu không: tự tìm file hợp lệ theo thứ tự ưu tiên (xem GetBestJsonPath)
    ///  - "/health"  -> 200 OK
    ///  - Static trong thư mục viewer (vd /assets/foo.js) nếu có
    /// </summary>
    public sealed class OnlineWatchServer : IDisposable
    {
        private readonly string _geoJson;     // geojson serialize sẵn (có thì ưu tiên)
        private readonly string _viewerPath;  // đường dẫn viewer.html
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private int _port;

        public OnlineWatchServer(string geoJson, string viewerHtmlPath)
        {
            _geoJson = geoJson; // có thể null -> sẽ tự tìm file
            _viewerPath = viewerHtmlPath ?? throw new ArgumentNullException(nameof(viewerHtmlPath));
            if (!File.Exists(_viewerPath)) throw new FileNotFoundException("Không thấy viewer.html", _viewerPath);
        }

        public string Start()
        {
            if (_listener != null) throw new InvalidOperationException("Server đã chạy.");
            _port = new Random().Next(49152, 65520);

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();

            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "OnlyWatchServer" };
            _thread.Start();

            return $"http://localhost:{_port}/";
        }

        private void Loop()
        {
            try
            {
                while (_running && _listener.IsListening)
                {
                    var ctx = _listener.GetContext(); // blocking
                    Handle(ctx);
                }
            }
            catch { /* stop */ }
            finally
            {
                try { _listener?.Stop(); } catch { }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
            string baseDir = Path.GetDirectoryName(_viewerPath)!;

            try
            {
                if (path == "" || path == "/")
                {
                    // Trả viewer.html
                    var html = File.ReadAllBytes(_viewerPath);
                    WriteHeaders(ctx, "text/html; charset=utf-8", noStore: true);
                    ctx.Response.ContentLength64 = html.LongLength;
                    ctx.Response.OutputStream.Write(html, 0, html.Length);
                    return;
                }

                if (path == "/health")
                {
                    WriteText(ctx, "OK", "text/plain; charset=utf-8", noStore: true);
                    return;
                }

                if (path == "/data")
                {
                    // 1) Ưu tiên geojson trong bộ nhớ (nếu có và không rỗng)
                    if (!string.IsNullOrWhiteSpace(_geoJson) && _geoJson != "{}")
                    {
                        WriteText(ctx, _geoJson, "application/json; charset=utf-8", noStore: true);
                        return;
                    }

                    // 2) Nếu có ?file=... -> đọc đúng file chỉ định
                    var q = ctx.Request.QueryString;
                    var fileParam = q["file"];
                    if (!string.IsNullOrWhiteSpace(fileParam))
                    {
                        var localFile = Uri.UnescapeDataString(fileParam);
                        if (!File.Exists(localFile))
                        {
                            ctx.Response.StatusCode = 404;
                            WriteText(ctx, $"File không tồn tại: {localFile}", "text/plain; charset=utf-8");
                            return;
                        }

                        string json = ReadTextMaybeGzip(localFile);
                        WriteText(ctx, json, "application/json; charset=utf-8", noStore: true);
                        return;
                    }

                    // 3) Tự động chọn file tốt nhất
                    var best = GetBestJsonPath(baseDir);
                    if (best == null)
                    {
                        // Không có file -> trả FeatureCollection rỗng để viewer vẫn chạy
                        WriteText(ctx,
                            "{\"type\":\"FeatureCollection\",\"features\":[]}",
                            "application/json; charset=utf-8",
                            noStore: true);
                        return;
                    }

                    string autoJson = ReadTextMaybeGzip(best);
                    WriteText(ctx, autoJson, "application/json; charset=utf-8", noStore: true);
                    return;
                }

                // Static file cùng thư mục viewer (để anh có thể nhúng js/css nếu muốn)
                var local = Path.Combine(baseDir, ctx.Request.Url!.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(local))
                {
                    var bytes = File.ReadAllBytes(local);
                    WriteHeaders(ctx, GuessContentType(local), noStore: true);
                    ctx.Response.ContentLength64 = bytes.LongLength;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    return;
                }

                // 404
                ctx.Response.StatusCode = 404;
                WriteText(ctx, "Not found", "text/plain; charset=utf-8");
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                WriteText(ctx, "Server error: " + ex.Message, "text/plain; charset=utf-8");
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        /// <summary>
        /// Chọn file JSON tốt nhất theo thứ tự ưu tiên:
        /// 1) Documents\RevitExports\revit_model.json.gz
        /// 2) Documents\RevitExports\revit_model.json
        /// 3) File .json/.json.gz mới nhất trong Documents\RevitExports
        /// 4) File .json/.json.gz mới nhất cùng thư mục viewer.html
        /// </summary>
        private static string? GetBestJsonPath(string viewerDir)
        {
            try
            {
                var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                var prefer1 = Path.Combine(docs, "revit_model.json.gz");
                var prefer2 = Path.Combine(docs, "revit_model.json");
                if (File.Exists(prefer1)) return prefer1;
                if (File.Exists(prefer2)) return prefer2;

                string newestInDocs = FindNewestJson(docs);
                if (newestInDocs != null) return newestInDocs;

                string newestInViewer = FindNewestJson(viewerDir);
                if (newestInViewer != null) return newestInViewer;
            }
            catch { /* ignore */ }
            return null;
        }

        private static string? FindNewestJson(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                var files = Directory
                    .EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                p.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase))
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .ToList();
                return files.FirstOrDefault()?.FullName;
            }
            catch { return null; }
        }

        private static string ReadTextMaybeGzip(string path)
        {
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(path);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var sr = new StreamReader(gz, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return sr.ReadToEnd();
            }
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static void WriteText(HttpListenerContext ctx, string text, string contentType, bool noStore = false)
        {
            WriteHeaders(ctx, contentType, noStore);
            var buf = Encoding.UTF8.GetBytes(text ?? string.Empty);
            ctx.Response.ContentLength64 = buf.LongLength;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        }

        private static void WriteHeaders(HttpListenerContext ctx, string contentType, bool noStore = false)
        {
            ctx.Response.ContentType = contentType;
            if (noStore)
            {
                ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                ctx.Response.Headers["Pragma"] = "no-cache";
                ctx.Response.Headers["Expires"] = "0";
            }
            // Cho phép xem ở origin khác nếu cần (viewer chạy file:// chẳng hạn)
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        }

        private static string GuessContentType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".mjs" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".gz" => "application/gzip",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
        }

        public void Dispose()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            try { _thread?.Interrupt(); } catch { }
            _thread = null;
        }
    }
}
