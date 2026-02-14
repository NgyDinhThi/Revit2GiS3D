using Autodesk.Revit.DB;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RevitToGISsupport.RemoteControl
{
    public static class RemoteGlbExporter
    {
        public static string ExportGlbForView(Document doc, View view, string folderPath)
        {
            if (doc == null || view == null) throw new ArgumentNullException("doc/view");
            Directory.CreateDirectory(folderPath);
            var glbPath = Path.Combine(folderPath, $"revit_view_{Guid.NewGuid():N}.glb");

            var collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
            var opt = new Options { View = view, ComputeReferences = false, IncludeNonVisibleObjects = false };

            ExportDirectly(collector, opt, glbPath);
            return glbPath;
        }

        public static string ExportGlbForProject(Document doc, string folderPath)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            Directory.CreateDirectory(folderPath);
            var glbPath = Path.Combine(folderPath, $"revit_project_{Guid.NewGuid():N}.glb");

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };

            ExportDirectly(collector, opt, glbPath);
            return glbPath;
        }

        private static void ExportDirectly(FilteredElementCollector collector, Options opt, string outputPath)
        {
            var triVerts = new List<float>();
            var triNormals = new List<float>();
            var triColors = new List<float>();
            var triIndices = new List<int>();
            int vertexOffset = 0;

            foreach (Element element in collector)
            {
                if (element == null) continue;
                GeometryElement geomElement = null;
                try { geomElement = element.get_Geometry(opt); } catch { }
                if (geomElement == null) continue;

                var catName = element.Category?.Name ?? "Unknown";
                var color = GetColorForCategory(catName);

                ProcessGeometry(geomElement, color, triVerts, triNormals, triColors, triIndices, ref vertexOffset);
            }

            if (triVerts.Count == 0)
                throw new Exception("Không có hình học 3D nào để xuất.");

            BuildGlb(outputPath, triVerts, triNormals, triColors, triIndices);
        }

        private static void ProcessGeometry(GeometryElement geomElement, (float r, float g, float b) color,
            List<float> verts, List<float> norms, List<float> colors, List<int> indices, ref int vOffset)
        {
            foreach (GeometryObject geomObj in geomElement)
            {
                if (geomObj is Solid solid && solid.Faces.Size > 0)
                {
                    foreach (Face face in solid.Faces)
                    {
                        Mesh mesh = null;
                        try { mesh = face.Triangulate(); } catch { }
                        if (mesh == null || mesh.NumTriangles == 0) continue;

                        for (int i = 0; i < mesh.NumTriangles; i++)
                        {
                            var tri = mesh.get_Triangle(i);
                            XYZ v0 = tri.get_Vertex(0);
                            XYZ v1 = tri.get_Vertex(1);
                            XYZ v2 = tri.get_Vertex(2);

                            // Tính Normal bề mặt
                            XYZ u = v1.Subtract(v0);
                            XYZ v = v2.Subtract(v0);
                            XYZ norm = u.CrossProduct(v);

                            // Sửa lật mặt (Winding fix)
                            if (norm.Z < 0)
                            {
                                var temp = v1;
                                v1 = v2;
                                v2 = temp;
                                norm = -norm;
                            }

                            norm = norm.Normalize();
                            if (norm.IsZeroLength()) norm = new XYZ(0, 0, 1);

                            // Đổi trục Z-up sang Y-up cho GLTF
                            float nx = (float)norm.X;
                            float ny = (float)norm.Z;
                            float nz = (float)-norm.Y;

                            AddVertex(v0, nx, ny, nz, color, verts, norms, colors);
                            AddVertex(v1, nx, ny, nz, color, verts, norms, colors);
                            AddVertex(v2, nx, ny, nz, color, verts, norms, colors);

                            indices.Add(vOffset++);
                            indices.Add(vOffset++);
                            indices.Add(vOffset++);
                        }
                    }
                }
                else if (geomObj is GeometryInstance instance)
                {
                    GeometryElement instGeom = null;
                    try { instGeom = instance.GetInstanceGeometry(); } catch { }
                    if (instGeom != null) ProcessGeometry(instGeom, color, verts, norms, colors, indices, ref vOffset);
                }
            }
        }

        private static void AddVertex(XYZ pt, float nx, float ny, float nz, (float r, float g, float b) color,
            List<float> verts, List<float> norms, List<float> colors)
        {
            float x = (float)UnitUtils.ConvertFromInternalUnits(pt.X, UnitTypeId.Meters);
            float y = (float)UnitUtils.ConvertFromInternalUnits(pt.Y, UnitTypeId.Meters);
            float z = (float)UnitUtils.ConvertFromInternalUnits(pt.Z, UnitTypeId.Meters);

            // Ghi tọa độ theo trục Y-up
            verts.Add(x);
            verts.Add(z);
            verts.Add(-y);

            norms.Add(nx);
            norms.Add(ny);
            norms.Add(nz);

            colors.Add(color.r);
            colors.Add(color.g);
            colors.Add(color.b);
        }

        // =========================================================
        // BINARY GLB PACKING
        // =========================================================

        private static void BuildGlb(string outputPath, List<float> triVerts, List<float> triNormals, List<float> triColors, List<int> triIndices)
        {
            byte[] posBytes = FloatListToBytes(triVerts);
            byte[] norBytes = FloatListToBytes(triNormals);
            byte[] colBytes = FloatListToBytes(triColors);

            bool useUshort = triIndices.Max() <= 0xFFFF;
            int idxComponentType = useUshort ? 5123 : 5125;
            byte[] idxBytes;

            if (useUshort)
            {
                idxBytes = new byte[triIndices.Count * 2];
                Buffer.BlockCopy(triIndices.Select(i => (ushort)i).ToArray(), 0, idxBytes, 0, idxBytes.Length);
            }
            else
            {
                idxBytes = new byte[triIndices.Count * 4];
                Buffer.BlockCopy(triIndices.Select(i => (uint)i).ToArray(), 0, idxBytes, 0, idxBytes.Length);
            }

            int offset = 0;
            int posOffset = 0; offset += posBytes.Length;
            int norOffset = offset; offset += norBytes.Length;
            int colOffset = offset; offset += colBytes.Length;
            int idxOffset = offset; offset += idxBytes.Length;

            int binLength = AlignTo4(offset);
            byte[] binChunk = new byte[binLength];
            Buffer.BlockCopy(posBytes, 0, binChunk, posOffset, posBytes.Length);
            Buffer.BlockCopy(norBytes, 0, binChunk, norOffset, norBytes.Length);
            Buffer.BlockCopy(colBytes, 0, binChunk, colOffset, colBytes.Length);
            Buffer.BlockCopy(idxBytes, 0, binChunk, idxOffset, idxBytes.Length);

            var minMax = ComputeMinMax(triVerts);

            var gltf = new Dictionary<string, object>
            {
                ["asset"] = new { version = "2.0", generator = "Revit Fast Hybrid Exporter" },
                ["buffers"] = new object[] { new { byteLength = binLength } },
                ["bufferViews"] = new object[] {
                    new { buffer = 0, byteOffset = posOffset, byteLength = posBytes.Length, target = 34962 },
                    new { buffer = 0, byteOffset = norOffset, byteLength = norBytes.Length, target = 34962 },
                    new { buffer = 0, byteOffset = colOffset, byteLength = colBytes.Length, target = 34962 },
                    new { buffer = 0, byteOffset = idxOffset, byteLength = idxBytes.Length, target = 34963 }
                },
                ["accessors"] = new object[] {
                    new { bufferView = 0, byteOffset = 0, componentType = 5126, count = triVerts.Count / 3, type = "VEC3", min = minMax.min, max = minMax.max },
                    new { bufferView = 1, byteOffset = 0, componentType = 5126, count = triNormals.Count / 3, type = "VEC3" },
                    new { bufferView = 2, byteOffset = 0, componentType = 5126, count = triColors.Count / 3, type = "VEC3" },
                    new { bufferView = 3, byteOffset = 0, componentType = idxComponentType, count = triIndices.Count, type = "SCALAR" }
                },
                ["meshes"] = new object[] {
                    new { primitives = new object[] { new { attributes = new { POSITION = 0, NORMAL = 1, COLOR_0 = 2 }, indices = 3, mode = 4 } } }
                },
                ["nodes"] = new object[] { new { mesh = 0 } },
                ["scenes"] = new object[] { new { nodes = new[] { 0 } } },
                ["scene"] = 0
            };

            var jsonStr = JsonConvert.SerializeObject(gltf);
            var jsonBytes = PadTo4(Encoding.UTF8.GetBytes(jsonStr), 0x20);

            using (var fs = new FileStream(outputPath, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(0x46546C67); // Header "glTF"
                bw.Write((uint)2);    // Version
                bw.Write((uint)(12 + 8 + jsonBytes.Length + 8 + binChunk.Length)); // Total Length

                bw.Write((uint)jsonBytes.Length);
                bw.Write(Encoding.ASCII.GetBytes("JSON"));
                bw.Write(jsonBytes);

                bw.Write((uint)binChunk.Length);
                bw.Write(Encoding.ASCII.GetBytes("BIN\0"));
                bw.Write(binChunk);
            }
        }

        private static byte[] FloatListToBytes(List<float> floats)
        {
            var b = new byte[floats.Count * sizeof(float)];
            Buffer.BlockCopy(floats.ToArray(), 0, b, 0, b.Length);
            return b;
        }

        private static byte[] PadTo4(byte[] bytes, byte padChar)
        {
            int pad = (4 - (bytes.Length % 4)) % 4;
            if (pad == 0) return bytes;
            var padded = new byte[bytes.Length + pad];
            Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
            for (int i = bytes.Length; i < padded.Length; i++) padded[i] = padChar;
            return padded;
        }

        private static int AlignTo4(int x) => (x + 3) & ~3;

        private static (double[] min, double[] max) ComputeMinMax(List<float> floats)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i + 2 < floats.Count; i += 3)
            {
                float x = floats[i], y = floats[i + 1], z = floats[i + 2];
                if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
                if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
            }
            if (minX == double.MaxValue) minX = minY = minZ = 0;
            if (maxX == double.MinValue) maxX = maxY = maxZ = 0;
            return (new[] { minX, minY, minZ }, new[] { maxX, maxY, maxZ });
        }

        private static (float r, float g, float b) GetColorForCategory(string categoryName)
        {
            using (var sha1 = SHA1.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(categoryName ?? "Unknown");
                var hash = sha1.ComputeHash(bytes);
                int hv = (hash[0] << 16) | (hash[1] << 8) | hash[2];
                double hue = (hv % 360) / 360.0;
                return HslToRgb(hue, 0.60, 0.55);
            }
        }

        private static (float r, float g, float b) HslToRgb(double h, double s, double l)
        {
            if (s <= 0.0) return ((float)l, (float)l, (float)l);
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

            return ((float)Hue2Rgb(p, q, h + 1.0 / 3), (float)Hue2Rgb(p, q, h), (float)Hue2Rgb(p, q, h - 1.0 / 3));
        }
    }
}