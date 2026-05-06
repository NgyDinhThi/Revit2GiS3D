using System;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace RevitToGISsupport.RemoteControl
{
    public static class RemoteSettings
    {
        // Thuộc tính này sẽ tự động gọi hàm FetchNgrokUrl() để lấy link khi Add-in khởi động
        public static string ServerBaseUrl { get; set; } = FetchNgrokUrl();

        public static string ProjectId { get; set; } = "P001";
        public static string TargetDocumentTitle { get; set; }

        // HÀM TỰ ĐỘNG BẮT LINK NGROK
        private static string FetchNgrokUrl()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    // Set timeout ngắn (2 giây) để nếu  quên bật ngrok, Add-in cũng không bị treo
                    client.Timeout = TimeSpan.FromSeconds(2);

                    // Gọi vào API nội bộ của Ngrok
                    var response = client.GetStringAsync("http://127.0.0.1:4040/api/tunnels").GetAwaiter().GetResult();

                    // Parse chuỗi JSON trả về
                    var json = JObject.Parse(response);
                    var tunnels = json["tunnels"];

                    // Duyệt qua các luồng tunnel để tìm link HTTPS
                    foreach (var tunnel in tunnels)
                    {
                        var publicUrl = tunnel["public_url"].ToString();
                        if (publicUrl.StartsWith("https"))
                        {
                            return publicUrl; // Đã tìm thấy link ngrok, trả về ngay!
                        }
                    }
                }
            }
            catch
            {
                // Nếu bị lỗi (ví dụ: do bạn chưa bật phần mềm Ngrok trên máy)
                // Nó sẽ tự động lùi về (fallback) dùng link localhost mặc định
            }

            return "http://127.0.0.1:5000";
        }
    }
}