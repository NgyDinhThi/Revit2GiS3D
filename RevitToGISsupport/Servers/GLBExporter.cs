using RevitToGISsupport.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RevitToGISsupport.Services
{
    public static class GLBExporter
    {
        // ==== Cấu hình nhanh ===================================================
        private const bool USE_LINES = true;      // bật/tắt viền line
        private const bool FIX_WINDING = true;    // đảo vòng nếu normal âm

        // Z-up (Revit/GIS) -> Y-up (glTF) + scale đơn vị
        // feet -> meters: 0.3048f ; mm -> meters: 0.001f ; meters: 1f
        private const float UNIT = 1f;

        // Bảng màu cố định (ưu tiên) cho một số category
        private static readonly Dictionary<string, (float r, float g, float b)> FixedPalette =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Walls"] = (0.85f, 0.40f, 0.95f),
                ["Floors"] = (1.00f, 0.50f, 0.65f),
                ["Roofs"] = (0.98f, 0.98f, 0.25f),
                ["Columns"] = (1.00f, 0.65f, 0.40f),
                ["Doors"] = (0.95f, 0.55f, 0.35f),
                ["Windows"] = (0.45f, 0.75f, 0.95f),
            };

        /// <summary>
        /// Xuất GLB gồm TRIANGLES (POSITION+NORMAL+COLOR_0), LINES (viền, tùy chọn) và POINTS (nếu có).
        /// ĐÃ đổi trục Z-up→Y-up ngay khi ghi dữ liệu + scale đơn vị.
        /// </summary>
        public static void ExportToGLB(GISStream stream, string outputPath)
        {
            if (stream == null || stream.objects == null || stream.objects.Count == 0)
                throw new ArgumentException("Stream is empty.");

            var triVerts = new List<float>();
            var triNormals = new List<float>();
            var triColors = new List<float>();
            var triIndices = new List<int>();

            var lineIndices = new List<int>();
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

                            // bỏ điểm đóng duplicate
                            if (ring.Count > 1)
                            {
                                var first = ring[0];
                                var last = ring[ring.Count - 1];
                                if (Math.Abs(first[0] - last[0]) < 1e-6 && Math.Abs(first[1] - last[1]) < 1e-6)
                                    ring.RemoveAt(ring.Count - 1);
                            }
                            if (ring.Count < 3) continue;

                            // fix winding nếu cần (theo Z-up trước khi transform)
                            var nTest = ComputeFaceNormal(ring);
                            if (FIX_WINDING && nTest[2] < 0) ring.Reverse();

                            var props = obj.properties ?? new Dictionary<string, object>();
                            var col = GetColorForCategory(props);
                            var nFlat = ComputeFaceNormal(ring); // normal trong Z-up

                            int baseOffset = triVertexOffset;
                            int added = 0;

                            foreach (var c in ring)
                            {
                                if (c.Count >= 3)
                                {
                                    // ---- Z-up -> Y-up + scale ----
                                    float x = (float)c[0];
                                    float y = (float)c[1];
                                    float z = (float)c[2];

                                    float xx = UNIT * x;
                                    float yy = UNIT * z;    // Y' = Z
                                    float zz = UNIT * (-y); // Z' = -Y

                                    triVerts.Add(xx);
                                    triVerts.Add(yy);
                                    triVerts.Add(zz);

                                    // ---- Transform normal tương ứng (chỉ xoay, không scale) ----
                                    float nx = (float)nFlat[0];
                                    float ny = (float)nFlat[1];
                                    float nz = (float)nFlat[2];

                                    float nx2 = nx;
                                    float ny2 = nz;   // Y' = Z
                                    float nz2 = -ny;  // Z' = -Y

                                    triNormals.Add(nx2);
                                    triNormals.Add(ny2);
                                    triNormals.Add(nz2);

                                    // ---- Color_0 ----
                                    triColors.Add(col.r);
                                    triColors.Add(col.g);
                                    triColors.Add(col.b);

                                    added++;
                                }
                            }

                            // triangulate theo fan (giữ nguyên logic cũ của chị; nếu muốn ear-clipping thì thay block này)
                            if (added >= 3)
                            {
                                for (int i = 1; i < added - 1; i++)
                                {
                                    triIndices.Add(triVertexOffset + 0);
                                    triIndices.Add(triVertexOffset + i);
                                    triIndices.Add(triVertexOffset + i + 1);
                                }
                            }

                            // lines (tùy chọn)
                            if (USE_LINES && added >= 2)
                            {
                                for (int i = 0; i < added - 1; i++)
                                {
                                    lineIndices.Add(baseOffset + i);
                                    lineIndices.Add(baseOffset + i + 1);
                                }
                                lineIndices.Add(baseOffset + (added - 1));
                                lineIndices.Add(baseOffset + 0);
                            }

                            triVertexOffset += added;
                        }
                    }
                    else if (type == "Point")
                    {
                        if (coordsObj is List<object> coords && coords.Count >= 3)
                        {
                            if (TryGet3D(coords, out var x, out var y, out var z))
                            {
                                // Z-up -> Y-up + scale
                                float xx = UNIT * (float)x;
                                float yy = UNIT * (float)z;
                                float zz = UNIT * (float)(-y);
                                pointVerts.Add(xx); pointVerts.Add(yy); pointVerts.Add(zz);
                            }
                        }
                        else if (coordsObj is List<double> coordsD && coordsD.Count >= 3)
                        {
                            float x = (float)coordsD[0];
                            float y = (float)coordsD[1];
                            float z = (float)coordsD[2];

                            float xx = UNIT * x;
                            float yy = UNIT * z;
                            float zz = UNIT * (-y);
                            pointVerts.Add(xx); pointVerts.Add(yy); pointVerts.Add(zz);
                        }
                    }
                }
            }

            if (triVerts.Count == 0 && pointVerts.Count == 0)
                throw new Exception("No geometry to export.");

            // ==== Build BIN =====================================================
            var triVertexBytes = FloatListToBytes(triVerts);
            var triNormalBytes = FloatListToBytes(triNormals);
            var triColorBytes = FloatListToBytes(triColors);
            var pointVertexBytes = FloatListToBytes(pointVerts);

            byte[] triIndexBytes = Array.Empty<byte>();
            int triIndexComponentType = 0;
            if (triIndices.Count > 0)
            {
                int maxIndex = triIndices.Max();
                if (maxIndex <= 0xFFFF)
                {
                    triIndexComponentType = 5123;
                    var us = triIndices.Select(i => (ushort)i).ToArray();
                    triIndexBytes = new byte[us.Length * sizeof(ushort)];
                    Buffer.BlockCopy(us, 0, triIndexBytes, 0, triIndexBytes.Length);
                }
                else
                {
                    triIndexComponentType = 5125;
                    var ui = triIndices.Select(i => (uint)i).ToArray();
                    triIndexBytes = new byte[ui.Length * sizeof(uint)];
                    Buffer.BlockCopy(ui, 0, triIndexBytes, 0, triIndexBytes.Length);
                }
            }

            byte[] lineIndexBytes = Array.Empty<byte>();
            int lineIndexComponentType = 0;
            if (USE_LINES && lineIndices.Count > 0)
            {
                int maxIndex = lineIndices.Max();
                if (maxIndex <= 0xFFFF)
                {
                    lineIndexComponentType = 5123;
                    var us = lineIndices.Select(i => (ushort)i).ToArray();
                    lineIndexBytes = new byte[us.Length * sizeof(ushort)];
                    Buffer.BlockCopy(us, 0, lineIndexBytes, 0, lineIndexBytes.Length);
                }
                else
                {
                    lineIndexComponentType = 5125;
                    var ui = lineIndices.Select(i => (uint)i).ToArray();
                    lineIndexBytes = new byte[ui.Length * sizeof(uint)];
                    Buffer.BlockCopy(ui, 0, lineIndexBytes, 0, lineIndexBytes.Length);
                }
            }

            int offset = 0;

            int triVertexOffsetBytes = AlignTo4(offset);
            offset = triVertexOffsetBytes + triVertexBytes.Length;

            int triNormalOffsetBytes = -1;
            if (triNormalBytes.Length > 0)
            {
                triNormalOffsetBytes = AlignTo4(offset);
                offset = triNormalOffsetBytes + triNormalBytes.Length;
            }

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

            int triIndexOffsetBytes = -1;
            if (triIndexBytes.Length > 0)
            {
                triIndexOffsetBytes = AlignTo4(offset);
                offset = triIndexOffsetBytes + triIndexBytes.Length;
            }

            int lineIndexOffsetBytes = -1;
            if (lineIndexBytes.Length > 0)
            {
                lineIndexOffsetBytes = AlignTo4(offset);
                offset = lineIndexOffsetBytes + lineIndexBytes.Length;
            }

            int totalBinLength = AlignTo4(offset);

            byte[] binChunk = new byte[totalBinLength];
            if (triVertexBytes.Length > 0) Buffer.BlockCopy(triVertexBytes, 0, binChunk, triVertexOffsetBytes, triVertexBytes.Length);
            if (triNormalBytes.Length > 0) Buffer.BlockCopy(triNormalBytes, 0, binChunk, triNormalOffsetBytes, triNormalBytes.Length);
            if (triColorBytes.Length > 0) Buffer.BlockCopy(triColorBytes, 0, binChunk, triColorOffsetBytes, triColorBytes.Length);
            if (pointVertexBytes.Length > 0) Buffer.BlockCopy(pointVertexBytes, 0, binChunk, pointVertexOffsetBytes, pointVertexBytes.Length);
            if (triIndexBytes.Length > 0) Buffer.BlockCopy(triIndexBytes, 0, binChunk, triIndexOffsetBytes, triIndexBytes.Length);
            if (lineIndexBytes.Length > 0) Buffer.BlockCopy(lineIndexBytes, 0, binChunk, lineIndexOffsetBytes, lineIndexBytes.Length);

            // ==== bufferViews & accessors ======================================
            var bufferViews = new List<object>();
            var accessors = new List<object>();

            int triPosAccessor = -1, triNorAccessor = -1, triColAccessor = -1;
            int pointPosAccessor = -1, triIdxAccessor = -1, lineIdxAccessor = -1;

            if (triVertexBytes.Length > 0)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = triVertexOffsetBytes, byteLength = triVertexBytes.Length, target = 34962 });
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
                triPosAccessor = accessors.Count - 1;
            }

            if (triNormalBytes.Length > 0)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = triNormalOffsetBytes, byteLength = triNormalBytes.Length, target = 34962 });
                accessors.Add(new
                {
                    bufferView = bufferViews.Count - 1,
                    byteOffset = 0,
                    componentType = 5126,
                    count = triNormals.Count / 3,
                    type = "VEC3"
                });
                triNorAccessor = accessors.Count - 1;
            }

            if (triColorBytes.Length > 0)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = triColorOffsetBytes, byteLength = triColorBytes.Length, target = 34962 });
                accessors.Add(new
                {
                    bufferView = bufferViews.Count - 1,
                    byteOffset = 0,
                    componentType = 5126,
                    count = triColors.Count / 3,
                    type = "VEC3"
                });
                triColAccessor = accessors.Count - 1;
            }

            if (pointVertexBytes.Length > 0)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = pointVertexOffsetBytes, byteLength = pointVertexBytes.Length, target = 34962 });
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
                pointPosAccessor = accessors.Count - 1;
            }

            if (triIndexBytes.Length > 0)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = triIndexOffsetBytes, byteLength = triIndexBytes.Length, target = 34963 });
                accessors.Add(new
                {
                    bufferView = bufferViews.Count - 1,
                    byteOffset = 0,
                    componentType = triIndexComponentType,
                    count = triIndices.Count,
                    type = "SCALAR"
                });
                triIdxAccessor = accessors.Count - 1;
            }

            if (lineIndexBytes.Length > 0)
            {
                bufferViews.Add(new { buffer = 0, byteOffset = lineIndexOffsetBytes, byteLength = lineIndexBytes.Length, target = 34963 });
                accessors.Add(new
                {
                    bufferView = bufferViews.Count - 1,
                    byteOffset = 0,
                    componentType = lineIndexComponentType,
                    count = lineIndices.Count,
                    type = "SCALAR"
                });
                lineIdxAccessor = accessors.Count - 1;
            }

            // ==== primitives & glTF ============================================
            var primitives = new List<object>();

            if (triPosAccessor >= 0)
            {
                var attrs = new Dictionary<string, int> { ["POSITION"] = triPosAccessor };
                if (triNorAccessor >= 0) attrs["NORMAL"] = triNorAccessor;
                if (triColAccessor >= 0) attrs["COLOR_0"] = triColAccessor;

                var prim = new Dictionary<string, object> { ["attributes"] = attrs, ["mode"] = 4 };
                if (triIdxAccessor >= 0) prim["indices"] = triIdxAccessor;
                primitives.Add(prim);
            }

            if (USE_LINES && lineIdxAccessor >= 0 && triPosAccessor >= 0)
            {
                var attrsLine = new Dictionary<string, int> { ["POSITION"] = triPosAccessor };
                if (triColAccessor >= 0) attrsLine["COLOR_0"] = triColAccessor;

                var primLines = new Dictionary<string, object>
                {
                    ["attributes"] = attrsLine,
                    ["mode"] = 1,
                    ["indices"] = lineIdxAccessor
                };
                primitives.Add(primLines);
            }

            if (pointPosAccessor >= 0)
            {
                primitives.Add(new Dictionary<string, object>
                {
                    ["attributes"] = new Dictionary<string, int> { ["POSITION"] = pointPosAccessor },
                    ["mode"] = 0
                });
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

            var jsonBytes = PadJson(JsonConvert.SerializeObject(gltf, Formatting.None));

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write(0x46546C67); // "glTF"
            bw.Write((uint)2);
            bw.Write((uint)(12 + 8 + jsonBytes.Length + 8 + binChunk.Length));

            bw.Write((uint)jsonBytes.Length);
            bw.Write(Encoding.ASCII.GetBytes("JSON"));
            bw.Write(jsonBytes);

            bw.Write((uint)binChunk.Length);
            bw.Write(Encoding.ASCII.GetBytes("BIN\0"));
            bw.Write(binChunk);

            Debug.WriteLine("GLB exported (Z-up->Y-up + unit scale): " + outputPath);
        }

        // ==== Helpers ==========================================================
        private static byte[] PadJson(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            int pad = (4 - (bytes.Length % 4)) % 4;
            if (pad == 0) return bytes;
            var padded = new byte[bytes.Length + pad];
            Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
            for (int i = bytes.Length; i < padded.Length; i++) padded[i] = 0x20;
            return padded;
        }

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
                                dd.Add(double.TryParse(pObj[k]?.ToString(), out double v) ? v : 0.0);
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

        private static double[] ComputeFaceNormal(List<List<double>> ring)
        {
            for (int i = 0; i < ring.Count - 2; i++)
            {
                var a = ring[i]; var b = ring[i + 1]; var c = ring[i + 2];
                var ux = b[0] - a[0]; var uy = b[1] - a[1]; var uz = b[2] - a[2];
                var vx = c[0] - a[0]; var vy = c[1] - a[1]; var vz = c[2] - a[2];
                var nx = uy * vz - uz * vy;
                var ny = uz * vx - ux * vz;
                var nz = ux * vy - uy * vx;
                var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-10) return new[] { nx / len, ny / len, nz / len };
            }
            return new[] { 0.0, 0.0, 1.0 };
        }

        private static string GetColorKey(Dictionary<string, object> props)
        {
            string Read(string k) =>
                (props != null && props.TryGetValue(k, out var v) && v != null) ? v.ToString().Trim() : null;

            var cat = Read("Category");
            var fam = Read("FamilyName");
            var type = Read("TypeName");
            var name = Read("Name");

            var key = !string.IsNullOrWhiteSpace(cat) ? cat :
                      !string.IsNullOrWhiteSpace(fam) ? $"Family:{fam}" :
                      !string.IsNullOrWhiteSpace(type) ? $"Type:{type}" :
                      !string.IsNullOrWhiteSpace(name) ? $"Name:{name}" : "Unknown";

            return Regex.Replace(key, @"\s+", " ").Trim();
        }

        private static (float r, float g, float b) GetColorForCategory(Dictionary<string, object> props)
        {
            var key = GetColorKey(props);
            if (FixedPalette.TryGetValue(key, out var fixedRgb))
                return fixedRgb;

            using var sha1 = SHA1.Create();
            var bytes = Encoding.UTF8.GetBytes(key ?? "Unknown");
            var hash = sha1.ComputeHash(bytes);
            int hv = (hash[0] << 16) | (hash[1] << 8) | hash[2];
            double hue = (hv % 360) / 360.0;
            var (r, g, b) = HslToRgb(hue, 0.60, 0.55);
            return ((float)r, (float)g, (float)b);
        }

        private static (double r, double g, double b) HslToRgb(double h, double s, double l)
        {
            if (s <= 0.0) return (l, l, l);
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