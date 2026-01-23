using System.Collections.Concurrent;

namespace RevitToGISsupport.RemoteControl
{
    public sealed class RemoteCommand
    {
        public string id { get; set; }
        public string action { get; set; }
        public string targetUniqueId { get; set; }
        public int pixelSize { get; set; } = 4000;
    }

    public static class RemoteCommandQueue
    {
        public static ConcurrentQueue<RemoteCommand> Items { get; } = new ConcurrentQueue<RemoteCommand>();
    }
}
