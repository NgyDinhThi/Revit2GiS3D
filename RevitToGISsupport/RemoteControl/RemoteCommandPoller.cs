using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using System.IO;

namespace RevitToGISsupport.RemoteControl
{
    public sealed class RemoteCommandPoller : IDisposable
    {
        // Key này phải khớp với app.py
        private const string API_KEY = "CHANGE-ME-IN-PRODUCTION";

        private readonly string _baseUrl;
        private readonly string _projectId;
        private readonly string _clientId;
        private readonly ExternalEvent _evt;
        private readonly Timer _timer;
        private readonly HttpClient _http;

        private int _ticking = 0;

        public RemoteCommandPoller(string baseUrl, string projectId, string clientId, ExternalEvent evt)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _projectId = projectId ?? "";
            _clientId = string.IsNullOrWhiteSpace(clientId) ? "default" : clientId;
            _evt = evt;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // [FIX QUAN TRỌNG] Thêm API Key vào Header mặc định cho Poller
            // Mọi request (Pull và Ack) từ Poller sẽ tự động có Key này
            _http.DefaultRequestHeaders.Add("X-API-Key", API_KEY);

            _timer = new Timer(async _ => await Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }

        private async Task Tick()
        {
            // ✅ chống chồng tick
            if (Interlocked.Exchange(ref _ticking, 1) == 1) return;

            try
            {
                var url = $"{_baseUrl}/api/projects/{_projectId}/commands/pull?clientId={Uri.EscapeDataString(_clientId)}";
                var json = await _http.GetStringAsync(url);

                var payload = JsonConvert.DeserializeObject<PullResponse>(json);
                var cmds = payload?.commands ?? new List<RemoteCommand>();
                if (cmds.Count == 0) return;

                var ackIds = new List<string>();

                foreach (var c in cmds)
                {
                    if (c == null) continue;
                    RemoteCommandQueue.Items.Enqueue(c);
                    if (!string.IsNullOrWhiteSpace(c.id)) ackIds.Add(c.id);
                }

                _evt?.Raise();

                // Gửi ACK để báo Server biết là đã nhận lệnh
                // Request này giờ đã có Header X-API-Key nhờ constructor ở trên
                var ackUrl = $"{_baseUrl}/api/projects/{_projectId}/commands/ack";
                var content = new StringContent(
                    JsonConvert.SerializeObject(new { ids = ackIds }),
                    System.Text.Encoding.UTF8,
                    "application/json"
                );

                await _http.PostAsync(ackUrl, content);
            }
            catch (Exception ex)
            {
                // Chỉ log lỗi vào file local để debug, tránh spam server
                try
                {
                    var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "revit_poller.log");
                    File.AppendAllText(logPath, $"{DateTime.Now:o} | Error: {ex.Message}{Environment.NewLine}");
                }
                catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _ticking, 0);
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _http?.Dispose();
        }

        private sealed class PullResponse
        {
            public List<RemoteCommand> commands { get; set; }
        }
    }
}