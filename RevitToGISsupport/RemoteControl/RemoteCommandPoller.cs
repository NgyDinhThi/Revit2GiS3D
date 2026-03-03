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
        private const string API_KEY = "CHANGE-ME-IN-PRODUCTION";

        private readonly string _baseUrl;
        private readonly string _projectId;
        private readonly string _clientId;
        private readonly ExternalEvent _evt;
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _cts;

        // Bộ nhớ lưu lại thời điểm cuối cùng nhận được lệnh từ Web
        private DateTime _lastActivityTime;

        // "Trí nhớ ngắn hạn" lưu ID các lệnh đã chạy trong phiên làm việc
        private static readonly HashSet<string> _executedCmds = new HashSet<string>();

        // [THÊM MỚI]: Hàm ép Plugin xóa trí nhớ, quên hết các lệnh đã làm
        public static void ClearMemory()
        {
            _executedCmds.Clear();
        }

        public RemoteCommandPoller(string baseUrl, string projectId, string clientId, ExternalEvent evt)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _projectId = projectId ?? "";
            _clientId = string.IsNullOrWhiteSpace(clientId) ? "default" : clientId;
            _evt = evt;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.Add("X-API-Key", API_KEY);

            _cts = new CancellationTokenSource();
            _lastActivityTime = DateTime.Now;

            // Khởi động luồng tuần tra ngầm
            Task.Run(() => PollingLoop(_cts.Token));
        }

        private async Task PollingLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var url = $"{_baseUrl}/api/projects/{_projectId}/commands/pull?clientId={Uri.EscapeDataString(_clientId)}";
                    var json = await _http.GetStringAsync(url);

                    var payload = JsonConvert.DeserializeObject<PullResponse>(json);
                    var cmds = payload?.commands ?? new List<RemoteCommand>();

                    if (cmds.Count > 0)
                    {
                        _lastActivityTime = DateTime.Now;
                        var ackIds = new List<string>();
                        bool hasNewCmd = false;

                        foreach (var c in cmds)
                        {
                            if (c == null) continue;

                            // Chỉ đưa lệnh vào hàng đợi NẾU NÓ CHƯA TỪNG ĐƯỢC CHẠY
                            if (!_executedCmds.Contains(c.id))
                            {
                                RemoteCommandQueue.Items.Enqueue(c);
                                _executedCmds.Add(c.id); // Khắc sâu vào trí nhớ là đã chạy rồi
                                hasNewCmd = true;
                            }

                            if (!string.IsNullOrWhiteSpace(c.id)) ackIds.Add(c.id);
                        }

                        // Chỉ đánh thức Revit dậy nếu thực sự có lệnh mới
                        if (hasNewCmd)
                        {
                            _evt?.Raise();
                        }

                        // Gửi báo cáo đã nhận cho Server
                        var ackUrl = $"{_baseUrl}/api/projects/{_projectId}/commands/ack";
                        var content = new StringContent(
                            JsonConvert.SerializeObject(new { ids = ackIds }),
                            System.Text.Encoding.UTF8,
                            "application/json"
                        );
                        await _http.PostAsync(ackUrl, content);
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "revit_poller.log");
                        File.AppendAllText(logPath, $"{DateTime.Now:o} | Error: {ex.Message}{Environment.NewLine}");
                    }
                    catch { }
                }

                int delayMs = CalculateAdaptiveDelay();

                try
                {
                    await Task.Delay(delayMs, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private int CalculateAdaptiveDelay()
        {
            var idleTime = DateTime.Now - _lastActivityTime;
            if (idleTime.TotalSeconds < 30) return 500;
            else if (idleTime.TotalMinutes < 5) return 2000;
            else return 5000;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _http?.Dispose();
        }

        private sealed class PullResponse
        {
            public List<RemoteCommand> commands { get; set; }
        }
    }
}