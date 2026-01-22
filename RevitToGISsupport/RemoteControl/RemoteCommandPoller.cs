using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Autodesk.Revit.UI;

namespace RevitToGISsupport.RemoteControl
{
    public sealed class RemoteCommandPoller : IDisposable
    {
        private readonly string _baseUrl;
        private readonly string _projectId;
        private readonly string _clientId;
        private readonly ExternalEvent _evt;
        private readonly Timer _timer;
        private readonly HttpClient _http;

        public RemoteCommandPoller(string baseUrl, string projectId, string clientId, ExternalEvent evt)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _projectId = projectId;
            _clientId = clientId;
            _evt = evt;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _timer = new Timer(async _ => await Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }

        private async System.Threading.Tasks.Task Tick()
        {
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

                var ackUrl = $"{_baseUrl}/api/projects/{_projectId}/commands/ack";
                var content = new StringContent(JsonConvert.SerializeObject(new { ids = ackIds }),
                    System.Text.Encoding.UTF8, "application/json");

                await _http.PostAsync(ackUrl, content);
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                            "revit_poller.log"),
                        DateTime.Now.ToString("o") + " | " + ex.ToString() + Environment.NewLine
                    );
                }
                catch { }
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
