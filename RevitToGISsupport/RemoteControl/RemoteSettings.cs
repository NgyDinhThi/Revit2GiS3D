namespace RevitToGISsupport.RemoteControl
{
    public static class RemoteSettings
    {
        public static string ServerBaseUrl { get; set; } = "http://127.0.0.1:5000";
        public static string ProjectId { get; set; } = "P001";
        public static string TargetDocumentTitle { get; set; }
    }
}