using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutocadToGISsupport.Models;
using System.Numerics;

namespace AutocadToGISsupport
{
    public class GLBExporter
    {
        // ExportToGLB: xuất stream sang file GLB.
        // - Bổ sung: dịch tâm (recenter) và tùy chọn chuẩn hoá (scale) để mô hình nằm gần origin
        //   giúp viewer tự động zoom/focus đúng và đọc thông tin dễ hơn.
        // - Param targetSize = 0f => chỉ recenter, không scale.
        public static void ExportToGLB(StreamData stream, string outputPath, bool recenter = true, float targetSize = 0f)
        {
            List<float> vertices = new List<float>();
            List<int> indices = new List<int>();
            int vertexOffset = 0;

            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            foreach (var feature in stream.Features)
            {
                if (feature?.Geometry?.Type == "Polygon" && feature.Geometry.Coordinates != null)
                {
                    var ring = feature.Geometry.Coordinates;
                    foreach (var c in ring)
                    {
                        if (c.Count >= 3)
                        {
                            float x = (float)c[0];
                            float y = (float)c[1];
                            float z = (float)c[2];

                            vertices.Add(x);
                            vertices.Add(y);
                            vertices.Add(z);

                            // Cập nhật min/max
                            min = Vector3.Min(min, new Vector3(x, y, z));
                            max = Vector3.Max(max, new Vector3(x, y, z));
                        }
                    }

                    for (int i = 1; i < ring.Count - 1; i++)
                    {
                        indices.Add(vertexOffset);
                        indices.Add(vertexOffset + i);
                        indices.Add(vertexOffset + i + 1);
                    }

                    vertexOffset += ring.Count;
                }
            }

            if (vertices.Count == 0)
                throw new Exception("❌ Không có dữ liệu để export GLB.");

            // --- RECENTER & SCALE STEP ---
            if (recenter)
            {
                Vector3 center = (min + max) * 0.5f;
                Vector3 extent = max - min;
                float maxExtent = Math.Max(Math.Max(extent.X, extent.Y), extent.Z);

                float scaleFactor = 1f;
                if (targetSize > 0f && maxExtent > 0f)
                    scaleFactor = targetSize / maxExtent;

                for (int i = 0; i < vertices.Count; i += 3)
                {
                    float x = vertices[i];
                    float y = vertices[i + 1];
                    float z = vertices[i + 2];

                    Vector3 v = new Vector3(x, y, z);
                    v = (v - center) * scaleFactor;

                    vertices[i] = v.X;
                    vertices[i + 1] = v.Y;
                    vertices[i + 2] = v.Z;
                }

                // cập nhật min/max sau transform
                min = new Vector3(float.MaxValue);
                max = new Vector3(float.MinValue);
                for (int i = 0; i < vertices.Count; i += 3)
                {
                    Vector3 v = new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]);
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            }
            // --- END RECENTER & SCALE STEP ---

            byte[] vertexBytes = new byte[vertices.Count * sizeof(float)];
            Buffer.BlockCopy(vertices.ToArray(), 0, vertexBytes, 0, vertexBytes.Length);

            byte[] indexBytes = new byte[indices.Count * sizeof(int)];
            Buffer.BlockCopy(indices.ToArray(), 0, indexBytes, 0, indexBytes.Length);

            var gltf = new
            {
                asset = new { version = "2.0", generator = "AutocadToGLB" },
                buffers = new[] { new { byteLength = vertexBytes.Length + indexBytes.Length } },
                bufferViews = new object[]
                {
                    new { buffer = 0, byteOffset = 0, byteLength = vertexBytes.Length, target = 34962 },
                    new { buffer = 0, byteOffset = vertexBytes.Length, byteLength = indexBytes.Length, target = 34963 }
                },
                accessors = new object[]
                {
                    new {
                        bufferView = 0,
                        componentType = 5126,
                        count = vertices.Count / 3,
                        type = "VEC3",
                        min = new float[] { min.X, min.Y, min.Z },
                        max = new float[] { max.X, max.Y, max.Z }
                    },
                    new {
                        bufferView = 1,
                        componentType = 5125,
                        count = indices.Count,
                        type = "SCALAR"
                    }
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

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(gltf, Newtonsoft.Json.Formatting.None);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

            int paddedJsonLength = (jsonBytes.Length + 3) & ~3;
            if (jsonBytes.Length < paddedJsonLength)
            {
                byte[] newJsonBytes = new byte[paddedJsonLength];
                Array.Copy(jsonBytes, newJsonBytes, jsonBytes.Length);
                for (int i = jsonBytes.Length; i < paddedJsonLength; i++)
                    newJsonBytes[i] = 0x20;
                jsonBytes = newJsonBytes;
            }

            byte[] binChunk = new byte[vertexBytes.Length + indexBytes.Length];
            Buffer.BlockCopy(vertexBytes, 0, binChunk, 0, vertexBytes.Length);
            Buffer.BlockCopy(indexBytes, 0, binChunk, vertexBytes.Length, indexBytes.Length);

            int paddedBinLength = (binChunk.Length + 3) & ~3;
            if (binChunk.Length < paddedBinLength)
            {
                byte[] newBinChunk = new byte[paddedBinLength];
                Array.Copy(binChunk, newBinChunk, binChunk.Length);
                for (int i = binChunk.Length; i < paddedBinLength; i++)
                    newBinChunk[i] = 0x00;
                binChunk = newBinChunk;
            }

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(0x46546C67); // "glTF"
                bw.Write(2);
                bw.Write(12 + 8 + jsonBytes.Length + 8 + binChunk.Length);

                bw.Write(jsonBytes.Length);
                bw.Write(0x4E4F534A); // "JSON"
                bw.Write(jsonBytes);

                bw.Write(binChunk.Length);
                bw.Write(0x004E4942); // "BIN"
                bw.Write(binChunk);
            }
        }
    }
}
