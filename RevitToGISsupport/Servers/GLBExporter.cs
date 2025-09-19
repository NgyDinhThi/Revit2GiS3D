using RevitToGISsupport.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RevitToGISsupport.Services
{
    public class GLBExporter
    {
        public static void ExportToGLB(GISStream stream, string outputPath)
        {
            List<float> vertices = new List<float>();
            List<int> indices = new List<int>();
            int vertexOffset = 0;

            foreach (var obj in stream.objects)
            {
                if (obj.geometry is Dictionary<string, object> geomDict &&
                    geomDict.ContainsKey("type") &&
                    geomDict.ContainsKey("coordinates"))
                {
                    string type = geomDict["type"].ToString();
                    if (type == "Polygon")
                    {
                        // ✅ Lấy dữ liệu dạng List<object>
                        if (geomDict["coordinates"] is List<object> coordsList &&
                            coordsList.Count > 0)
                        {
                            // coordsList[0] chính là 1 ring (list các điểm)
                            if (coordsList[0] is List<List<double>> ring)
                            {
                                foreach (var c in ring)
                                {
                                    if (c.Count >= 3)
                                    {
                                        vertices.Add((float)c[0]);
                                        vertices.Add((float)c[1]);
                                        vertices.Add((float)c[2]);
                                    }
                                }

                                // triangulate fan
                                for (int i = 1; i < ring.Count - 1; i++)
                                {
                                    indices.Add(vertexOffset);
                                    indices.Add(vertexOffset + i);
                                    indices.Add(vertexOffset + i + 1);
                                }

                                vertexOffset += ring.Count;
                            }
                        }
                    }
                }
            }

            if (vertices.Count == 0)
                throw new Exception("❌ Không có dữ liệu để export GLB.");

            // 🔹 Tạo buffer binary
            byte[] vertexBytes = new byte[vertices.Count * sizeof(float)];
            Buffer.BlockCopy(vertices.ToArray(), 0, vertexBytes, 0, vertexBytes.Length);

            byte[] indexBytes = new byte[indices.Count * sizeof(int)];
            Buffer.BlockCopy(indices.ToArray(), 0, indexBytes, 0, indexBytes.Length);

            int vertexOffsetBytes = 0;
            int indexOffsetBytes = vertexBytes.Length;

            // 🔹 JSON GLTF nội bộ
            var gltf = new
            {
                asset = new { version = "2.0", generator = "RevitToGISsupport" },
                buffers = new[] { new { byteLength = vertexBytes.Length + indexBytes.Length } },
                bufferViews = new object[]
                {
                    new { buffer = 0, byteOffset = vertexOffsetBytes, byteLength = vertexBytes.Length, target = 34962 },
                    new { buffer = 0, byteOffset = indexOffsetBytes, byteLength = indexBytes.Length, target = 34963 }
                },
                accessors = new object[]
                {
                    new { bufferView = 0, componentType = 5126, count = vertices.Count/3, type = "VEC3" },
                    new { bufferView = 1, componentType = 5125, count = indices.Count, type = "SCALAR" }
                },
                meshes = new object[]
                {
                    new {
                        primitives = new object[]
                        {
                            new {
                                attributes = new { POSITION = 0 },
                                indices = 1
                            }
                        }
                    }
                },
                nodes = new object[] { new { mesh = 0 } },
                scenes = new object[] { new { nodes = new int[] { 0 } } },
                scene = 0
            };

            // 🔹 Serialize JSON
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(gltf, Newtonsoft.Json.Formatting.None);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

            // pad json chunk multiple of 4
            int paddedJsonLength = (jsonBytes.Length + 3) & ~3;
            Array.Resize(ref jsonBytes, paddedJsonLength);

            // pad bin chunk multiple of 4
            byte[] binChunk = new byte[vertexBytes.Length + indexBytes.Length];
            Buffer.BlockCopy(vertexBytes, 0, binChunk, 0, vertexBytes.Length);
            Buffer.BlockCopy(indexBytes, 0, binChunk, vertexBytes.Length, indexBytes.Length);

            int paddedBinLength = (binChunk.Length + 3) & ~3;
            Array.Resize(ref binChunk, paddedBinLength);

            // 🔹 Write GLB file
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                // header
                bw.Write(0x46546C67); // magic "glTF"
                bw.Write(2); // version
                bw.Write(12 + 8 + jsonBytes.Length + 8 + binChunk.Length); // total length

                // JSON chunk
                bw.Write(jsonBytes.Length);
                bw.Write(0x4E4F534A); // "JSON"
                bw.Write(jsonBytes);

                // BIN chunk
                bw.Write(binChunk.Length);
                bw.Write(0x004E4942); // "BIN"
                bw.Write(binChunk);
            }
        }
    }
}
