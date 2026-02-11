using System.Collections.Concurrent;
using System.Collections.Generic; 

namespace RevitToGISsupport.RemoteControl
{
    public sealed class RemoteCommand
    {
        public string id { get; set; }
        public string action { get; set; }
        public string targetUniqueId { get; set; }
        public int pixelSize { get; set; } = 4000;

        public Dictionary<string, string> parameters { get; set; }
    }

    public static class RemoteCommandQueue
    {
        public static ConcurrentQueue<RemoteCommand> Items { get; } = new ConcurrentQueue<RemoteCommand>();
    }
}