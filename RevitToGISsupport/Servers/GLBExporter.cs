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
        // public entry
        public static void ExportToGLB(GISStream stream, string outputPath)
        {
            if (stream == null || stream.objects == null || stream.objects.Count == 0)
                throw new ArgumentException("Stream is empty.");

            // Collect geometry: separate triangle vertices/indices and standalone points
            var triVerts = new List<float>();     // floats triples
            var triIndices = new List<int>();     // triangle indices (will convert to ushort/uint)
            var pointVerts = new List<float>();   // point vertices triples

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
                        // expect coordinates: List<object> where first item is a ring (List<List<double>>)
                        if (coordsObj is List<object> coordsList && coordsList.Count > 0)
                        {
                            // parse ring
                            List<List<double>> ring = ParseRing(coordsList[0]);
                            if (ring == null || ring.Count < 3) continue;

                            // remove possible duplicate last == first
                            if (ring.Count > 1)
                            {
                                var first = ring[0];
                                var last = ring[ring.Count - 1];
                                if (Math.Abs(first[0] - last[0]) < 1e-6 && Math.Abs(first[1] - last[1]) < 1e-6)
                                {
                                    ring.RemoveAt(ring.Count - 1);
                                }
                            }

                            int added = 0;
                            foreach (var c in ring)
                            {
                                if (c.Count >= 3)
                                {
                                    triVerts.Add((float)c[0]);
                                    triVerts.Add((float)c[1]);
                                    triVerts.Add((float)c[2]);
                                    added++;
                                }
                            }

                            if (added >= 3)
                            {
                                // triangulate as fan (0, i, i+1)
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
                        // expect coordinates: List<double> of length >= 3
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

            // Convert to byte arrays
            var triVertexBytes = FloatListToBytes(triVerts);
            var pointVertexBytes = FloatListToBytes(pointVerts);

            // Indices: choose ushort or uint
            byte[] indexBytes = Array.Empty<byte>();
            int indexComponentType = 0; // 5123=USHORT, 5125=UINT
            if (triIndices.Count > 0)
            {
                int maxIndex = triIndices.Max();
                if (maxIndex <= 0xFFFF)
                {
                    // use ushort
                    indexComponentType = 5123;
                    ushort[] us = new ushort[triIndices.Count];
                    for (int i = 0; i < triIndices.Count; i++) us[i] = (ushort)triIndices[i];
                    indexBytes = new byte[us.Length * sizeof(ushort)];
                    Buffer.BlockCopy(us, 0, indexBytes, 0, indexBytes.Length);
                }
                else
                {
                    // use uint
                    indexComponentType = 5125;
                    uint[] ui = new uint[triIndices.Count];
                    for (int i = 0; i < triIndices.Count; i++) ui[i] = (uint)triIndices[i];
                    indexBytes = new byte[ui.Length * sizeof(uint)];
                    Buffer.BlockCopy(ui, 0, indexBytes, 0, indexBytes.Length);
                }
            }

            // Build bin buffer layout with 4-byte alignment for each bufferView
            int offset = 0;
            int triVertexOffsetBytes = AlignTo4(offset);
            offset = triVertexOffsetBytes + triVertexBytes.Length;

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
            if (triVertexBytes.Length > 0)
                Buffer.BlockCopy(triVertexBytes, 0, binChunk, triVertexOffsetBytes, triVertexBytes.Length);
            if (pointVertexBytes.Length > 0)
                Buffer.BlockCopy(pointVertexBytes, 0, binChunk, pointVertexOffsetBytes, pointVertexBytes.Length);
            if (indexBytes.Length > 0)
                Buffer.BlockCopy(indexBytes, 0, binChunk, indexOffsetBytes, indexBytes.Length);
            // rest of binChunk is already 0

            // build GLTF json structure
            var bufferViews = new List<object>();
            var accessors = new List<object>();

            // bufferView 0: tri positions (if any)
            int triAccessorIndex = -1, pointAccessorIndex = -1, indexAccessorIndex = -1;
            int bufferIndex = 0;

            if (triVertexBytes.Length > 0)
            {
                bufferViews.Add(new
                {
                    buffer = 0,
                    byteOffset = triVertexOffsetBytes,
                    byteLength = triVertexBytes.Length,
                    target = 34962
                });
                // compute min/max for tri positions
                var (minVec, maxVec) = ComputeMinMax(triVerts);
                accessors.Add(new
                {
                    bufferView = bufferViews.Count - 1,
                    byteOffset = 0,
                    componentType = 5126,
                    count = triVerts.Count / 3,
                    type = "VEC3",
                    min = new[] { minVec[0], minVec[1], minVec[2] },
                    max = new[] { maxVec[0], maxVec[1], maxVec[2] }
                });
                triAccessorIndex = accessors.Count - 1;
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
                accessors.Add(new
                {
                    bufferView = bufferViews.Count - 1,
                    byteOffset = 0,
                    componentType = 5126,
                    count = pointVerts.Count / 3,
                    type = "VEC3"
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
                    count = (indexComponentType == 5123 ? triIndices.Count : triIndices.Count),
                    type = "SCALAR"
                });
                indexAccessorIndex = accessors.Count - 1;
            }

            // Build mesh primitives
            var primitives = new List<object>();
            if (triAccessorIndex >= 0)
            {
                var prim = new Dictionary<string, object>();
                prim["attributes"] = new Dictionary<string, int> { ["POSITION"] = triAccessorIndex };
                prim["indices"] = indexAccessorIndex;
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

            if (primitives.Count == 0)
                throw new Exception("No primitives to export.");

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

            // Serialize JSON and pad with spaces (0x20)
            string json = JsonConvert.SerializeObject(gltf, Formatting.None);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
            if (jsonPad > 0)
            {
                var padded = new byte[jsonBytes.Length + jsonPad];
                Buffer.BlockCopy(jsonBytes, 0, padded, 0, jsonBytes.Length);
                for (int i = jsonBytes.Length; i < padded.Length; i++) padded[i] = 0x20; // spaces
                jsonBytes = padded;
            }

            // binChunk already padded to multiple of 4
            // Write GLB
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(0x46546C67); // "glTF"
                bw.Write((uint)2); // version
                uint totalLength = (uint)(12 + 8 + jsonBytes.Length + 8 + binChunk.Length);
                bw.Write(totalLength);

                // JSON chunk
                bw.Write((uint)jsonBytes.Length);
                bw.Write(Encoding.ASCII.GetBytes("JSON"));
                bw.Write(jsonBytes);

                // BIN chunk
                bw.Write((uint)binChunk.Length);
                bw.Write(Encoding.ASCII.GetBytes("BIN\0"));
                bw.Write(binChunk);
            }

            Debug.WriteLine("GLB exported: " + outputPath);
        }

        // helpers
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

        private static bool TryGet3D(List<object> coords, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (coords == null || coords.Count < 3) return false;
            if (!double.TryParse(coords[0].ToString(), out x)) return false;
            if (!double.TryParse(coords[1].ToString(), out y)) return false;
            if (!double.TryParse(coords[2].ToString(), out z)) return false;
            return true;
        }

        private static byte[] FloatListToBytes(List<float> floats)
        {
            if (floats == null || floats.Count == 0) return Array.Empty<byte>();
            var b = new byte[floats.Count * sizeof(float)];
            Buffer.BlockCopy(floats.ToArray(), 0, b, 0, b.Length);
            return b;
        }

        private static int AlignTo4(int x) => (x + 3) & ~3;

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
    }
}
