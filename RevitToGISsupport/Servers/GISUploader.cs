using Newtonsoft.Json;
using RevitToGISsupport.Models;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace RevitToGISsupport.Services
{
    public class GISUploader
    {
        public async Task<bool> Send(GISStream stream)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(5);

                var json = JsonConvert.SerializeObject(stream.ToGeoJson());
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                try
                {
                    var url = "http://127.0.0.1:5000/upload"; 
                    var response = await client.PostAsync(url, content);

                    if (!response.IsSuccessStatusCode)
                    {
                        string responseBody = await response.Content.ReadAsStringAsync();
                        System.Diagnostics.Debug.WriteLine($"❌ Upload failed: {response.StatusCode} - {responseBody}");
                    }

                    return response.IsSuccessStatusCode;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"❌ Exception during upload: {ex.Message}");
                    return false;
                }
            }
        }
    }

}
