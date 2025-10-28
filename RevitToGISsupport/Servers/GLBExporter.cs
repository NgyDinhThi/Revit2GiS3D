using RevitToGISsupport.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Linq;

namespace RevitToGISsupport.Services
{
    public static class GLBExporter
    {
        /// <summary>
        /// Xuất GLB: POSITION + COLOR_0 (màu theo Category), kèm POINTS nếu có.
        /// </summary>
        public static void ExportToGLB(GISStream stream, string outputPath)
        {
            if (stream == null || stream.objects == null || stream.objects.Count == 0)
                throw new ArgumentException("Stream is empty.");

            var triVerts = new List<float>();
            var triColors = new List<float>();   // song hành với triVerts
            var triIndices = new List<int>();
            var pointVerts = new List<float>();
            int triVertexOffset = 0;

            foreach (var obj in stream.objects)
            {
                if (obj.geometry is Dictionary<string, object> geomDict &&
                    geomDict.TryGetValue("type", out var t) &&
                    geomDict.TryGetValue("coordinates", out var coordsObj))
                {
                    var type = t?.ToString();

                    if (type == "Polygon")
                    {
                        if (coordsObj is List<object> coordsList && coordsList.Count > 0)
                        {
                            var ring = ParseRing(coordsList[0]);
                            if (ring == null || ring.Count < 3) continue;

                            if (ring.Count > 1)
                            {
                                var first = ring[0];
                                var last = ring[ring.Count - 1];
                                if (Math.Abs(first[0] - last[0]) < 1e-6 && Math.Abs(first[1] - last[1]) < 1e-6)
                                    ring.RemoveAt(ring.Count - 1);
                            }

                            var props = obj.properties ?? new Dictionary<string, object>();
                            var col = GetColorForCategory(props);

                            int added = 0;
                            foreach (var c in ring)
                            {
                                if (c.Count >= 3)
                                {
                                    triVerts.Add((float)c[0]);
                                    triVerts.Add((float)c[1]);
                                    triVerts.Add((float)c[2]);

                                    triColors.Add(col.r);
                                    triColors.Add(col.g);
                                    triColors.Add(col.b);

                                    added++;
                                }
                            }

                            if (added >= 3)
                            {
                                for (int i = 1; i < added - 1; i++)
                                {
                                    triIndices.Add(triVertexOffset + 0);
                                    triIndices.Add(triVertexOffset + i);
                                    triIndices.Add(triVertexOffset + i + 1);
                                }
                                triVertexOffset += added;
                            }
                        }
                    }
                    else if (type == "Point")
                    {
                        if (coordsObj is List<object> coords && coords.Count >= 3)
                        {
                            if (TryGet3D(coords, out var x, out var y, out var z))
                            {
                                pointVerts.Add((float)x);
                                pointVerts.Add((float)y);
                                pointVerts.Add((float)z);
                            }
                        }
                        else if (coordsObj is List<double> coordsD && coordsD.Count >= 3)
                        {
                            pointVerts.Add((float)coordsD[0]);
                            pointVerts.Add((float)coordsD[1]);
                            pointVerts.Add((float)coordsD[2]);
                        }
                    }
                }
            }

            if (triVerts.Count == 0 && pointVerts.Count == 0)
                throw new Exception("No geometry to export.");

            var triVertexBytes = FloatListToBytes(triVerts);
            var triColorBytes = FloatListToBytes(triColors);
            var pointVertexBytes = FloatListToBytes(pointVerts);

            byte[] indexBytes = Array.Empty<byte>();
            int indexComponentType = 0;
            if (triIndices.Count > 0)
            {
                int maxIndex = triIndices.Max();
                if (maxIndex <= 0xFFFF)
                {
                    indexComponentType = 5123; // USHORT
                    ushort[] us = new ushort[triIndices.Count];
                    for (int i = 0; i < triIndices.Count; i++) us[i] = (ushort)triIndices[i];
                    indexBytes = new byte[us.Length * sizeof(ushort)];
                    Buffer.BlockCopy(us, 0, indexBytes, 0, indexBytes.Length);
                }
                else
                {
                    indexComponentType = 5125; // UINT
                    uint[] ui = new uint[triIndices.Count];
                    for (int i = 0; i < triIndices.Count; i++) ui[i] = (uint)triIndices[i];
                    indexBytes = new byte[ui.Length * sizeof(uint)];
                    Buffer.BlockCopy(ui, 0, indexBytes, 0, indexBytes.Length);
                }
            }

            int offset = 0;

            int triVertexOffsetBytes = AlignTo4(offset);
            offset = triVertexOffsetBytes + triVertexBytes.Length;

            int triColorOffsetBytes = -1;
            if (triColorBytes.Length > 0)
            {
                triColorOffsetBytes = AlignTo4(offset);
                offset = triColorOffsetBytes + triColorBytes.Length;
            }

            int pointVertexOffsetBytes = -1;
            if (pointVertexBytes.Length > 0)
            {
                pointVertexOffsetBytes = AlignTo4(offset);
                offset = pointVertexOffsetBytes + pointVertexBytes.Length;
            }

            int indexOffsetBytes = -1;
            if (indexBytes.Length > 0)
            {
                indexOffsetBytes = AlignTo4(offset);
                offset = indexOffsetBytes + indexBytes.Length;
            }

            int totalBinLength = AlignTo4(offset);

            byte[] binChunk = new byte[totalBinLength];
            if (triVertexBytes.Length > 0) Buffer.BlockCopy(triVertexBytes, 0, binChunk, triVertexOffsetBytes, triVertexBytes.Length);
            if (triColorBytes.Length > 0) Buffer.BlockCopy(triColorBytes, 0, binChunk, triColorOffsetBytes, triColorBytes.Length);
            if (pointVertexBytes.Length > 0) Buffer.BlockCopy(pointVertexBytes, 0, binChunk, pointVertexOffsetBytes, pointVertexBytes.Length);
            if (indexBytes.Length > 0) Buffer.BlockCopy(indexBytes, 0, binChunk, indexOffsetBytes, indexBytes.Length);

            var bufferViews = new List<object>();
            var accessors = new List<object>();

            int triAccessorIndex = -1, triColorAccessorIndex = -1, pointAccessorIndex = -1, indexAccessorIndex = -1;

            if (triVertexBytes.Length > 0)
            {
                bufferViews.Add(new
                {
                    buffer = 0,
                    byteOffset = triVertexOffsetBytes,
                    byteLength = triVertexBytes.Length,
                    target = 34962
                });
                var (minT, maxT) = ComputeMinMax(triVerts);
                accessors.Add(new
                {
                    bufferView = bufferViews.Count - 1,
                    byteOffset = 0,
                    componentType = 5126,
                    count = triVerts.Count / 3,
                    type = "VEC3",
                    min = new[] { minT[0], minT[1], minT[2] },
                    max = new[] { maxT[0], maxT[1], maxT[2] }
                });
                triAccessorIndex = accessors.Count - 1;
            }

            if (triColorBytes.Length > 0)
            {
                bufferViews.Add(new
                {
                    buffer = 0,
                    byteOffset = triColorOffsetBytes,
                    byteLength = triColorBytes.Length,
                    target = 34962
                });
                accessors.Add(new
                {
                    bufferView = bufferViews.Count - 1,
                    byteOffset = 0,
                    componentType = 5126, // FLOAT
                    count = triColors.Count / 3,
                    type = "VEC3"
                });
                triColorAccessorIndex = accessors.Count - 1;
            }

            if (pointVertexBytes.Length > 0)
            {
                bufferViews.Add(new
                {
                    buffer = 0,
                    byteOffset = pointVertexOffsetBytes,
                    byteLength = pointVertexBytes.Length,
                    target = 34962
                });
                var (minP, maxP) = ComputeMinMax(pointVerts);
                accessors.Add(new
                {
                    bufferView = bufferViews.Count - 1,
                    byteOffset = 0,
                    componentType = 5126,
                    count = pointVerts.Count / 3,
                    type = "VEC3",
                    min = new[] { minP[0], minP[1], minP[2] },
                    max = new[] { maxP[0], maxP[1], maxP[2] }
                });
                pointAccessorIndex = accessors.Count - 1;
            }

            if (indexBytes.Length > 0)
            {
                bufferViews.Add(new
                {
                    buffer = 0,
                    byteOffset = indexOffsetBytes,
                    byteLength = indexBytes.Length,
                    target = 34963
                });
                accessors.Add(new
                {
                    bufferView = bufferViews.Count - 1,
                    byteOffset = 0,
                    componentType = indexComponentType,
                    count = triIndices.Count,
                    type = "SCALAR"
                });
                indexAccessorIndex = accessors.Count - 1;
            }

            var primitives = new List<object>();
            if (triAccessorIndex >= 0)
            {
                var prim = new Dictionary<string, object>();
                var attrs = new Dictionary<string, int> { ["POSITION"] = triAccessorIndex };
                if (triColorAccessorIndex >= 0) attrs["COLOR_0"] = triColorAccessorIndex;
                prim["attributes"] = attrs;
                if (indexAccessorIndex >= 0) prim["indices"] = indexAccessorIndex;
                prim["mode"] = 4; // TRIANGLES
                primitives.Add(prim);
            }

            if (pointAccessorIndex >= 0)
            {
                var primP = new Dictionary<string, object>();
                primP["attributes"] = new Dictionary<string, int> { ["POSITION"] = pointAccessorIndex };
                primP["mode"] = 0; // POINTS
                primitives.Add(primP);
            }

            if (primitives.Count == 0) throw new Exception("No primitives to export.");

            var gltf = new Dictionary<string, object>
            {
                ["asset"] = new { version = "2.0", generator = "RevitToGISsupport" },
                ["buffers"] = new[] { new { byteLength = totalBinLength } },
                ["bufferViews"] = bufferViews.ToArray(),
                ["accessors"] = accessors.ToArray(),
                ["meshes"] = new object[] { new { primitives = primitives.ToArray() } },
                ["nodes"] = new object[] { new { mesh = 0 } },
                ["scenes"] = new object[] { new { nodes = new int[] { 0 } } },
                ["scene"] = 0
            };

            string json = JsonConvert.SerializeObject(gltf, Formatting.None);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
            if (jsonPad > 0)
            {
                var padded = new byte[jsonBytes.Length + jsonPad];
                Buffer.BlockCopy(jsonBytes, 0, padded, 0, jsonBytes.Length);
                for (int i = jsonBytes.Length; i < padded.Length; i++) padded[i] = 0x20;
                jsonBytes = padded;
            }

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(0x46546C67); // "glTF"
                bw.Write((uint)2);
                uint totalLength = (uint)(12 + 8 + jsonBytes.Length + 8 + binChunk.Length);
                bw.Write(totalLength);

                bw.Write((uint)jsonBytes.Length);
                bw.Write(Encoding.ASCII.GetBytes("JSON"));
                bw.Write(jsonBytes);

                bw.Write((uint)binChunk.Length);
                bw.Write(Encoding.ASCII.GetBytes("BIN\0"));
                bw.Write(binChunk);
            }

            Debug.WriteLine("GLB exported: " + outputPath);
        }

        /// <summary>
        /// Parse một ring toạ độ thành List<List<double>> (x,y,z).
        /// </summary>
        private static List<List<double>> ParseRing(object ringObj)
        {
            try
            {
                if (ringObj is List<List<double>> typed) return typed;
                if (ringObj is List<object> objList)
                {
                    var outList = new List<List<double>>();
                    foreach (var p in objList)
                    {
                        if (p is List<object> pObj && pObj.Count >= 3)
                        {
                            var dd = new List<double>();
                            for (int k = 0; k < 3; k++)
                            {
                                if (double.TryParse(pObj[k].ToString(), out double v)) dd.Add(v);
                                else dd.Add(0.0);
                            }
                            outList.Add(dd);
                        }
                        else if (p is List<double> pd && pd.Count >= 3)
                        {
                            outList.Add(new List<double> { pd[0], pd[1], pd[2] });
                        }
                    }
                    return outList.Count > 0 ? outList : null;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Lấy (x,y,z) từ list object.
        /// </summary>
        private static bool TryGet3D(List<object> coords, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (coords == null || coords.Count < 3) return false;
            if (!double.TryParse(coords[0].ToString(), out x)) return false;
            if (!double.TryParse(coords[1].ToString(), out y)) return false;
            if (!double.TryParse(coords[2].ToString(), out z)) return false;
            return true;
        }

        /// <summary>
        /// Chuyển list float sang mảng byte.
        /// </summary>
        private static byte[] FloatListToBytes(List<float> floats)
        {
            if (floats == null || floats.Count == 0) return Array.Empty<byte>();
            var b = new byte[floats.Count * sizeof(float)];
            Buffer.BlockCopy(floats.ToArray(), 0, b, 0, b.Length);
            return b;
        }

        /// <summary>
        /// Căn 4 byte.
        /// </summary>
        private static int AlignTo4(int x) => (x + 3) & ~3;

        /// <summary>
        /// Tính min/max theo trục cho POSITION.
        /// </summary>
        private static (double[] min, double[] max) ComputeMinMax(List<float> floats)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i + 2 < floats.Count; i += 3)
            {
                double x = floats[i], y = floats[i + 1], z = floats[i + 2];
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (z < minZ) minZ = z;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                if (z > maxZ) maxZ = z;
            }
            if (minX == double.MaxValue) minX = minY = minZ = 0;
            if (maxX == double.MinValue) maxX = maxY = maxZ = 0;
            return (new[] { minX, minY, minZ }, new[] { maxX, maxY, maxZ });
        }

        /// <summary>
        /// Map Category -> màu RGB ổn định.
        /// </summary>
        private static (float r, float g, float b) GetColorForCategory(Dictionary<string, object> props)
        {
            string cat = (props != null && props.TryGetValue("Category", out var v) && v != null)
                            ? v.ToString()
                            : "Unknown";
            int hash = cat.GetHashCode();
            double hue = ((hash & 0x7fffffff) % 360) / 360.0;
            var (r, g, b) = HslToRgb(hue, 0.60, 0.55);
            return ((float)r, (float)g, (float)b);
        }

        /// <summary>
        /// HSL(0..1) -> RGB(0..1).
        /// </summary>
        private static (double r, double g, double b) HslToRgb(double h, double s, double l)
        {
            if (s <= 0.0)
                return (l, l, l);

            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;

            double Hue2Rgb(double pp, double qq, double tt)
            {
                if (tt < 0) tt += 1;
                if (tt > 1) tt -= 1;
                if (tt < 1.0 / 6) return pp + (qq - pp) * 6 * tt;
                if (tt < 1.0 / 2) return qq;
                if (tt < 2.0 / 3) return pp + (qq - pp) * (2.0 / 3 - tt) * 6;
                return pp;
            }

            double r = Hue2Rgb(p, q, h + 1.0 / 3);
            double g = Hue2Rgb(p, q, h);
            double b = Hue2Rgb(p, q, h - 1.0 / 3);
            return (r, g, b);
        }
    }
}
