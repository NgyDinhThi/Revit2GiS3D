using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RevitToGISsupport.Models;

namespace RevitToGISsupport.Utils
{
    public static class JsonLoader
    {
        /// <summary>
        /// Đọc GISStream từ .json hoặc .json.gz bằng streaming, không block UI.
        /// </summary>
        public static async Task<GISStream> LoadStreamAsync(string path)
        {
            return await Task.Run(() =>
            {
                var ext = Path.GetExtension(path)?.ToLowerInvariant();
                Stream baseStream = File.OpenRead(path);

                // Nếu là .gz thì bọc GZipStream
                if (ext == ".gz")
                    baseStream = new GZipStream(baseStream, CompressionMode.Decompress);

                using (baseStream)
                using (var sr = new StreamReader(baseStream))
                using (var jr = new JsonTextReader(sr))
                {
                    var serializer = new JsonSerializer();
                    return serializer.Deserialize<GISStream>(jr);
                }
            });
        }
    }
}
