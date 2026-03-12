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

        private DateTime _lastActivityTime;
        private static readonly HashSet<string> _executedCmds = new HashSet<string>();

        public static void ClearMemory() { _executedCmds.Clear(); }

        public RemoteCommandPoller(string baseUrl, string projectId, string clientId, string userName, ExternalEvent evt)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _projectId = projectId ?? "";
            _clientId = string.IsNullOrWhiteSpace(clientId) ? "default" : clientId;
            _evt = evt;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.Add("X-API-Key", API_KEY);

            // [ĐÃ SỬA] Mã hóa URL danh tính
            _http.DefaultRequestHeaders.Add("X-User-Name", Uri.EscapeDataString(userName));

            _cts = new CancellationTokenSource();
            _lastActivityTime = DateTime.Now;

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
                            if (!_executedCmds.Contains(c.id))
                            {
                                RemoteCommandQueue.Items.Enqueue(c);
                                _executedCmds.Add(c.id);
                                hasNewCmd = true;
                            }
                            if (!string.IsNullOrWhiteSpace(c.id)) ackIds.Add(c.id);
                        }
                        if (hasNewCmd) _evt?.Raise();
                        var ackUrl = $"{_baseUrl}/api/projects/{_projectId}/commands/ack";
                        var content = new StringContent(JsonConvert.SerializeObject(new { ids = ackIds }), System.Text.Encoding.UTF8, "application/json");
                        await _http.PostAsync(ackUrl, content);
                    }
                }
                catch { }

                int delayMs = CalculateAdaptiveDelay();
                try { await Task.Delay(delayMs, token); } catch { break; }
            }
        }

        private int CalculateAdaptiveDelay()
        {
            var idleTime = DateTime.Now - _lastActivityTime;
            if (idleTime.TotalSeconds < 30) return 500;
            else if (idleTime.TotalMinutes < 5) return 2000;
            else return 5000;
        }

        public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); _http?.Dispose(); }
        private sealed class PullResponse { public List<RemoteCommand> commands { get; set; } }
    }
}