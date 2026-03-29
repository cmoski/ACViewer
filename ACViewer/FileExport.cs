using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

/*using Aspose.ThreeD;
using Aspose.ThreeD.Animation;
using Aspose.ThreeD.Entities;*/

using Assimp;

using UkooLabs.FbxSharpie;

using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;

using ACE.Entity.Enum;

using ACViewer.Enum;
using ACViewer.Extensions;
using ACViewer.Model;
using ACViewer.View;

using Matrix4x4 = System.Numerics.Matrix4x4;
using UkooLabs.FbxSharpie.Tokens.Value;
using UkooLabs.FbxSharpie.Tokens;

namespace ACViewer
{
    public static class FileExport
    {
        public static bool ExportRaw(DatType datType, uint fileID, string outFilename)
        {
            DatDatabase datDatabase = null;

            switch (datType)
            {
                case DatType.Cell:
                    datDatabase = DatManager.CellDat;
                    break;
                case DatType.Portal:
                    datDatabase = DatManager.PortalDat;
                    break;
                case DatType.HighRes:
                    datDatabase = DatManager.HighResDat;
                    break;
                case DatType.Language:
                    datDatabase = DatManager.LanguageDat;
                    break;
            }

            if (datDatabase == null) return false;

            var datReader = datDatabase.GetReaderForFile(fileID);

            if (datReader == null) return false;

            var maxFileSize = 10000000;

            using (var memoryStream = new MemoryStream(datReader.Buffer))
            {
                using (var reader = new BinaryReader(memoryStream))
                {
                    var bytes = reader.ReadBytes(maxFileSize);
                    Console.WriteLine($"Read {bytes.Length} bytes");

                    File.WriteAllBytes(outFilename, bytes);
                }
            }
            MainWindow.Instance.AddStatusText($"Wrote {outFilename}");
            return true;
        }

        public static bool ExportModel(uint fileID, string outFilename)
        {
            if (fileID >> 24 != 0x1 && fileID >> 24 != 0x2)
            {
                Console.WriteLine($"Unknown model file: {fileID:X8}");
                return false;
            }

            var sb = new StringBuilder();

            sb.AppendLine($"# {fileID:X8}");
            sb.AppendLine();
            sb.AppendLine($"mtllib {fileID:X8}.mtl");

            var surfaceIDs = new Dictionary<uint, bool>();

            var isSetup = fileID >> 24 == 0x2;

            var startIdx = 0;
            var startUVIdx = 0;

            if (isSetup)
            {
                var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(fileID);

                List<Frame> placementFrames = null;

                if (setup.PlacementFrames.TryGetValue((int)Placement.Resting, out var placement) || setup.PlacementFrames.TryGetValue((int)Placement.Default, out placement))
                    placementFrames = placement.AnimFrame.Frames;

                for (var i = 0; i < setup.Parts.Count; i++)
                {
                    var part = setup.Parts[i];

                    if (part == 0x010001ec)   // skip anchor locations
                        continue;

                    var transform = Matrix4x4.Identity;

                    if (i < setup.DefaultScale.Count && setup.DefaultScale[i] != Vector3.One)
                        transform = Matrix4x4.CreateScale(setup.DefaultScale[i]);

                    if (placementFrames != null && i < placementFrames.Count)
                    {
                        var partFrame = placementFrames[i];

                        transform *= Matrix4x4.CreateFromQuaternion(partFrame.Orientation) * Matrix4x4.CreateTranslation(partFrame.Origin);
                    }

                    sb.AppendLine();
                    sb.AppendLine($"# {part:X8}");
                    sb.AppendLine();

                    ExportGfxObj(part, sb, ref startIdx, ref startUVIdx, transform, surfaceIDs);
                }
            }
            else
            {
                sb.AppendLine();
                ExportGfxObj(fileID, sb, ref startIdx, ref startUVIdx, Matrix4x4.Identity, surfaceIDs);
            }

            File.WriteAllText(outFilename, sb.ToString());
            MainWindow.Instance.AddStatusText($"Wrote {outFilename}");

            Console.Write(sb.ToString());

            Console.WriteLine();

            var fi = new System.IO.FileInfo(outFilename);
            var mtlFilename = fi.DirectoryName + Path.DirectorySeparatorChar + $"{fileID:X8}.mtl";

            ExportSurfaces(fileID, surfaceIDs, mtlFilename);

            return true;
        }

        private static void ExportGfxObj(uint gfxObjID, StringBuilder sb, ref int startIdx, ref int startUVIdx, Matrix4x4 transform, Dictionary<uint, bool> surfaceIDs)
        {
            var gfxObj = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.GfxObj>(gfxObjID);

            // vertices
            var vertices = gfxObj.VertexArray.Vertices.OrderBy(i => i.Key).Select(i => i.Value).ToList();

            // directx -> opengl / left-hand -> right-hand
            // model viewer also has y & z swapped

            // 0x020000A7 is a good test for maintaining UV's -- note the rivet locations on the top vs. sides
            // 0x02000001 is also a good test for final model orientation

            foreach (var _v in vertices)
            {
                var v = Vector3.Transform(_v.Origin, transform);
                sb.AppendLine($"v {-v.X} {v.Z} {v.Y}");
            }
            sb.AppendLine();

            // texture coordinates
            var vertexUVs = new Dictionary<VertexUV, int>();
            var nextUvIdx = 0;

            for (var i = 0; i < vertices.Count(); i++)
            {
                var v = vertices[i];

                for (var j = 0; j < v.UVs.Count; j++)
                {
                    var uv = v.UVs[j];

                    sb.AppendLine($"vt {uv.U} {-uv.V}");
                    vertexUVs.Add(new VertexUV(i, j), nextUvIdx++);
                }
            }
            sb.AppendLine();

            // normals
            foreach (var _v in gfxObj.VertexArray.Vertices.OrderBy(i => i.Key).Select(i => i.Value))
            {
                var v = Vector3.Transform(_v.Normal, transform);
                sb.AppendLine($"vn {-v.X} {v.Z} {v.Y}");
            }

            var si = startIdx;
            uint lastSurfaceId = 0;

            // polygons
            foreach (var poly in gfxObj.Polygons.OrderBy(i => i.Key).Select(i => i.Value))
            {
                var currentSurfaceId = gfxObj.Surfaces[poly.PosSurface];

                if (currentSurfaceId != lastSurfaceId)
                {
                    sb.AppendLine();
                    sb.AppendLine($"usemtl {currentSurfaceId:X8}");
                    sb.AppendLine();
                    lastSurfaceId = currentSurfaceId;
                }

                var polyStr = "f";

                for (var i = 0; i < poly.VertexIds.Count; i++)
                {
                    var v = poly.VertexIds[i];
                    var uvIdx = vertexUVs[new VertexUV(v, i < poly.PosUVIndices.Count ? poly.PosUVIndices[i] : 0)];     // investigate: some polys dont have uv arrays?
                    polyStr += $" {v + startIdx + 1}/{uvIdx + startUVIdx + 1}/{v + startIdx + 1}";
                }
                sb.AppendLine(polyStr);
            }

            startIdx += gfxObj.VertexArray.Vertices.Count;
            startUVIdx += vertexUVs.Count;

            var _gfxObj = GfxObjCache.Get(gfxObjID);

            foreach (var surfaceID in gfxObj.Surfaces)
            {
                var existing = surfaceIDs.TryGetValue(surfaceID, out var hasWrappingUVs);

                if (existing)
                {
                    if (!hasWrappingUVs && _gfxObj.HasWrappingUVs)
                        surfaceIDs[surfaceID] = true;
                }
                else
                    surfaceIDs.Add(surfaceID, _gfxObj.HasWrappingUVs);
            }
        }

        private static void ExportSurfaces(uint fileID, Dictionary<uint, bool> surfaceIDs, string outFilename)
        {
            var fi = new System.IO.FileInfo(outFilename);

            var sb = new StringBuilder();

            sb.AppendLine($"# {fileID:X8}");

            foreach (var kvp in surfaceIDs)
            {
                var surfaceID = kvp.Key;
                var hasWrappingUVs = kvp.Value;

                var surfaceFilename = fi.DirectoryName + Path.DirectorySeparatorChar + $"{surfaceID:X8}.png";

                if (!File.Exists(surfaceFilename))
                    ExportImage(surfaceID, surfaceFilename);

                //var options = hasWrappingUVs ? "" : "-clamp on ";     // doesn't work??
                var options = "";

                sb.AppendLine();
                sb.AppendLine($"newmtl {surfaceID:X8}");
                //sb.AppendLine($"Ka 1 1 1");
                //sb.AppendLine($"Kd 1 1 1");
                //sb.AppendLine($"Ks 0 0 0");
                //sb.AppendLine($"map_Ka {options}{surfaceID:X8}.png");
                sb.AppendLine($"map_Kd {options}{surfaceID:X8}.png");
                //sb.AppendLine($"map_Ks {options}{surfaceID:X8}.png");
            }

            File.WriteAllText(outFilename, sb.ToString());

            Console.WriteLine(sb.ToString());

            /*foreach (var surfaceID in surfaceIDs.Keys)
                Console.WriteLine($"Exported {surfaceID:X8}.png");

            Console.WriteLine();*/
        }

        public static bool ExportImage(uint fileID, string outFilename)
        {
            var fileType = fileID >> 24;

            if (fileType != 0x5 && fileType != 0x6 && fileType != 0x8)
            {
                Console.WriteLine($"Unknown image file: {fileID:X8}");
                return false;
            }

            if (fileType == 0x8)
            {
                var surface = DatManager.PortalDat.ReadFromDat<Surface>(fileID);
                fileID = surface.OrigTextureId;
                fileType = 0x05;
            }

            Bitmap highRes = null;

            if (fileType == 0x5)
            {
                var surfaceTexture = DatManager.PortalDat.ReadFromDat<SurfaceTexture>(fileID);

                // since previous file dialog had user enter a filename, 
                // only export highest resolution texture as that filename

                foreach (var textureID in surfaceTexture.Textures)
                {
                    var bitmap = GetBitmap(textureID);

                    if (bitmap != null && (highRes == null || bitmap.Width * bitmap.Height > highRes.Width * highRes.Height))
                        highRes = bitmap;
                }
            }
            else
                highRes = GetBitmap(fileID);

            if (highRes == null) return false;

            highRes.Save(outFilename);

            MainWindow.Instance.AddStatusText($"Wrote {outFilename}");
            return true;
        }

        private static Bitmap GetBitmap(uint textureID)
        {
            var texture = DatManager.PortalDat.ReadFromDat<Texture>(textureID);

            if (texture.Id == 0 && DatManager.HighResDat != null)
                texture = DatManager.HighResDat.ReadFromDat<Texture>(textureID);

            if (texture.Id == 0) return null;

            return texture.GetBitmap();
        }

        public static bool ExportSound(uint fileID, string outFilename)
        {
            var fileType = fileID >> 24;

            if (fileType != 0xA)
            {
                Console.WriteLine($"Unknown audio file: {fileID:X8}");
                return false;
            }
            var sound = DatManager.PortalDat.ReadFromDat<Wave>(fileID);

            using (var f = new FileStream(outFilename, FileMode.Create))
            {
                sound.ReadData(f);
                f.Close();
            }
            MainWindow.Instance.AddStatusText($"Wrote {outFilename}");
            return true;
        }

        // ===============================

        /*public static bool ExportModel_Aspose(uint fileID, MotionData motionData, string outFilename)
        {
            if (fileID >> 24 != 0x1 && fileID >> 24 != 0x2)
            {
                Console.WriteLine($"Unknown model file: {fileID:X8}");
                return false;
            }

            var scene = new Aspose.ThreeD.Scene();

            var surfaceIDs = new Dictionary<uint, bool>();

            var isSetup = fileID >> 24 == 0x2;

            if (isSetup)
            {
                var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(fileID);

                List<Frame> placementFrames = null;

                if (setup.PlacementFrames.TryGetValue((int)Placement.Resting, out var placement) || setup.PlacementFrames.TryGetValue((int)Placement.Default, out placement))
                    placementFrames = placement.AnimFrame.Frames;

                for (var i = 0; i < setup.Parts.Count; i++)
                {
                    var gfxObjId = setup.Parts[i];

                    if (gfxObjId == 0x010001ec)   // skip anchor locations
                        continue;

                    var meshNode = new Aspose.ThreeD.Node($"{gfxObjId:X8}");    // check for duplicates?

                    if (i < setup.DefaultScale.Count && setup.DefaultScale[i] != Vector3.One)
                        meshNode.Transform.SetScale(setup.DefaultScale[i].X, setup.DefaultScale[i].Z, setup.DefaultScale[i].Y);

                    if (placementFrames != null && i < placementFrames.Count)
                    {
                        var partFrame = placementFrames[i];

                        meshNode.Transform.SetTranslation(-partFrame.Origin.X, partFrame.Origin.Z, partFrame.Origin.Y);

                        var q = new System.Numerics.Quaternion(-partFrame.Orientation.X, partFrame.Orientation.Z, partFrame.Orientation.Y, partFrame.Orientation.W);
                        var a = q.ToEulerAngles();

                        meshNode.Transform.EulerAngles = new Aspose.ThreeD.Utilities.Vector3(a.X.ToDegs(), a.Y.ToDegs(), a.Z.ToDegs());
                    }

                    var mesh = ExportGfxObj_Aspose(gfxObjId, Matrix4x4.Identity, surfaceIDs);

                    meshNode.Entity = mesh;

                    ExportSurfaces_Aspose(fileID, surfaceIDs, outFilename, meshNode);

                    scene.RootNode.ChildNodes.Add(meshNode);
                }
            }
            else
            {
                var mesh = ExportGfxObj_Aspose(fileID, Matrix4x4.Identity, surfaceIDs);

                var meshNode = new Aspose.ThreeD.Node($"{fileID:X8}");
                meshNode.Entity = mesh;

                ExportSurfaces_Aspose(fileID, surfaceIDs, outFilename, meshNode);

                scene.RootNode.ChildNodes.Add(meshNode);
            }

            if (motionData != null)
                BuildAnimation_Aspose(scene, motionData);

            //TrialException.SuppressTrialException = true;

            if (outFilename.EndsWith(".fbx"))
            {
                scene.Save(outFilename, FileFormat.FBX7700Binary);
                MainWindow.Instance.AddStatusText($"Wrote {outFilename}");

                //var outTextFilename = outFilename.Replace(".fbx", "-text.fbx");
                //scene.Save(outTextFilename, FileFormat.FBX7700ASCII);
                //MainWindow.Instance.AddStatusText($"Wrote {outTextFilename}");
            }
            else if (outFilename.EndsWith(".dae"))
            {
                scene.Save(outFilename, FileFormat.Collada);
                MainWindow.Instance.AddStatusText($"Wrote {outFilename}");
            }
            else
                return false;

            return true;
        }

        private static Aspose.ThreeD.Entities.Mesh ExportGfxObj_Aspose(uint gfxObjID, Matrix4x4 transform, Dictionary<uint, bool> surfaceIDs)
        {
            var gfxObj = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.GfxObj>(gfxObjID);

            // vertices
            var vertices = gfxObj.VertexArray.Vertices.OrderBy(i => i.Key).Select(i => i.Value).ToList();

            // directx -> opengl / left-hand -> right-hand
            // model viewer also has y & z swapped

            // 0x020000A7 is a good test for maintaining UV's -- note the rivet locations on the top vs. sides
            // 0x02000001 is also a good test for final model orientation

            var mesh = new Aspose.ThreeD.Entities.Mesh();

            foreach (var _v in vertices)
            {
                var v = Vector3.Transform(_v.Origin, transform);
                mesh.ControlPoints.Add(new Aspose.ThreeD.Utilities.Vector4(-v.X, v.Z, v.Y, 1.0));
            }

            // texture coordinates
            var vertexUVs = new Dictionary<VertexUV, int>();
            var nextUvIdx = 0;
            var uvs = new List<Aspose.ThreeD.Utilities.Vector4>();

            for (var i = 0; i < vertices.Count(); i++)
            {
                var v = vertices[i];

                for (var j = 0; j < v.UVs.Count; j++)
                {
                    var uv = v.UVs[j];

                    uvs.Add(new Aspose.ThreeD.Utilities.Vector4(uv.U, -uv.V, 0.0, 1.0));

                    vertexUVs.Add(new VertexUV(i, j), nextUvIdx++);
                }
            }

            // normals
            var normals = new List<Aspose.ThreeD.Utilities.Vector4>();

            foreach (var _v in gfxObj.VertexArray.Vertices.OrderBy(i => i.Key).Select(i => i.Value))
            {
                var v = Vector3.Transform(_v.Normal, transform);
                normals.Add(new Aspose.ThreeD.Utilities.Vector4(-v.X, v.Z, v.Y, 1.0));
            }

            //var si = startIdx;
            uint lastSurfaceId = 0;

            // polygons
            var builder = new PolygonBuilder(mesh);
            var uvIndices = new List<int>();
            var matIndices = new List<int>();

            foreach (var poly in gfxObj.Polygons.OrderBy(i => i.Key).Select(i => i.Value))
            {
                var currentSurfaceId = gfxObj.Surfaces[poly.PosSurface];

                if (currentSurfaceId != lastSurfaceId)
                    lastSurfaceId = currentSurfaceId;

                matIndices.Add(poly.PosSurface);

                builder.Begin();

                for (var i = 0; i < poly.VertexIds.Count; i++)
                {
                    var v = poly.VertexIds[i];
                    var uvIdx = vertexUVs[new VertexUV(v, i < poly.PosUVIndices.Count ? poly.PosUVIndices[i] : 0)];     // investigate: some polys dont have uv arrays?

                    //polyStr += $" {v + startIdx + 1}/{uvIdx + startUVIdx + 1}/{v + startIdx + 1}";
                    builder.AddVertex(v);
                    uvIndices.Add(uvIdx);
                }
                //sb.AppendLine(polyStr);
                builder.End();
            }

            var elementNormal = mesh.CreateElement(VertexElementType.Normal, MappingMode.ControlPoint, ReferenceMode.Direct) as VertexElementNormal;
            elementNormal.Data.AddRange(normals);

            var elementUV = mesh.CreateElementUV(Aspose.ThreeD.Entities.TextureMapping.Diffuse, MappingMode.PolygonVertex, ReferenceMode.IndexToDirect);
            elementUV.Data.AddRange(uvs);
            elementUV.Indices.AddRange(uvIndices);

            var elementMats = mesh.CreateElement(VertexElementType.Material, MappingMode.Polygon, ReferenceMode.IndexToDirect) as VertexElementMaterial;
            elementMats.Indices.AddRange(matIndices);

            var _gfxObj = GfxObjCache.Get(gfxObjID);

            foreach (var surfaceID in gfxObj.Surfaces)
            {
                var existing = surfaceIDs.TryGetValue(surfaceID, out var hasWrappingUVs);

                if (existing)
                {
                    if (!hasWrappingUVs && _gfxObj.HasWrappingUVs)
                        surfaceIDs[surfaceID] = true;
                }
                else
                    surfaceIDs.Add(surfaceID, _gfxObj.HasWrappingUVs);
            }

            return mesh;
        }

        private static void ExportSurfaces_Aspose(uint fileID, Dictionary<uint, bool> surfaceIDs, string outFilename, Aspose.ThreeD.Node meshNode)
        {
            var fi = new System.IO.FileInfo(outFilename);

            foreach (var kvp in surfaceIDs)
            {
                var surfaceID = kvp.Key;
                //var hasWrappingUVs = kvp.Value;

                var surfaceFilename = fi.DirectoryName + Path.DirectorySeparatorChar + $"{surfaceID:X8}.png";

                if (!File.Exists(surfaceFilename))
                    ExportImage(surfaceID, surfaceFilename);

                var diffuse = new Aspose.ThreeD.Shading.Texture();

                diffuse.FileName = $"{surfaceID:X8}.png";
                //diffuse.Content = File.ReadAllBytes(surfaceFilename);   // embed actual data, instead of linking to external filename - fbx feature only?

                var material = new Aspose.ThreeD.Shading.PhongMaterial();
                //var material = new Aspose.ThreeD.Shading.LambertMaterial();
                material.Name = $"{surfaceID:X8}";
                material.SetTexture("DiffuseColor", diffuse);
                material.SpecularFactor = 0.0f;
                material.ReflectionFactor = 0.0f;

                meshNode.Materials.Add(material);
            }

            surfaceIDs.Clear();
        }

        private static void BuildAnimation_Aspose(Aspose.ThreeD.Scene scene, MotionData motionData)
        {
            // create bindpoints
            var bpTranslate = new List<BindPoint>();
            var bpRotate = new List<BindPoint>();

            foreach (var meshNode in scene.RootNode.ChildNodes)
            {
                var translate = new BindPoint(scene, meshNode.Transform.FindProperty("Translation"));
                translate.BindKeyframeSequence("X", new KeyframeSequence());
                translate.BindKeyframeSequence("Y", new KeyframeSequence());
                translate.BindKeyframeSequence("Z", new KeyframeSequence());
                bpTranslate.Add(translate);

                //var rotate = new BindPoint(scene, meshNode.Transform.FindProperty("Rotation"));
                var rotate = new BindPoint(scene, meshNode.Transform.FindProperty("EulerAngles"));
                rotate.BindKeyframeSequence("X", new KeyframeSequence());
                rotate.BindKeyframeSequence("Y", new KeyframeSequence());
                rotate.BindKeyframeSequence("Z", new KeyframeSequence());
                //rotate.BindKeyframeSequence("W", new KeyframeSequence());
                bpRotate.Add(rotate);
            }

            var interpMode = Interpolation.Constant;

            foreach (var animData in motionData.Anims)
            {
                var perFrame = 1.0f / animData.Framerate;

                var anim = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.Animation>(animData.AnimId);

                for (var animFrameIdx = 0; animFrameIdx < anim.PartFrames.Count; animFrameIdx++)
                {
                    var curTime = perFrame * animFrameIdx;
                    var partFrames = anim.PartFrames[animFrameIdx];

                    for (var partIdx = 0; partIdx < partFrames.Frames.Count; partIdx++)
                    {
                        var partFrame = partFrames.Frames[partIdx];

                        bpTranslate[partIdx].GetKeyframeSequence("X").Add(curTime, -partFrame.Origin.X, interpMode);
                        bpTranslate[partIdx].GetKeyframeSequence("Y").Add(curTime, partFrame.Origin.Z, interpMode);
                        bpTranslate[partIdx].GetKeyframeSequence("Z").Add(curTime, partFrame.Origin.Y, interpMode);

                        var q = new System.Numerics.Quaternion(-partFrame.Orientation.X, partFrame.Orientation.Z, partFrame.Orientation.Y, partFrame.Orientation.W);
                        var a = q.ToEulerAngles();

                        bpRotate[partIdx].GetKeyframeSequence("X").Add(curTime, a.X.ToDegs(), interpMode);
                        bpRotate[partIdx].GetKeyframeSequence("Y").Add(curTime, a.Y.ToDegs(), interpMode);
                        bpRotate[partIdx].GetKeyframeSequence("Z").Add(curTime, a.Z.ToDegs(), interpMode);
                        //bpRotate[partIdx].GetKeyframeSequence("W").Add(curTime, partFrame.Orientation.W, interpMode);
                    }
                }
            }
        }*/

        // ===============================

        public static bool ExportModel_Assimp(uint fileID, MotionData motionData, string outFilename)
        {
            if (fileID >> 24 != 0x1 && fileID >> 24 != 0x2)
            {
                Console.WriteLine($"Unknown model file: {fileID:X8}");
                return false;
            }

            var scene = new Assimp.Scene();
            scene.RootNode = new Assimp.Node();

            var isSetup = fileID >> 24 == 0x2;

            var materialIdx = new Dictionary<uint, MaterialIdx>();

            // assimp animations don't link up to mesh nodes by id or hierarchy,
            // and must link up by unique node names!
            var gfxObjIdCnts = new Dictionary<uint, int>();

            if (isSetup)
            {
                var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(fileID);

                List<Frame> placementFrames = null;

                if (setup.PlacementFrames.TryGetValue((int)Placement.Resting, out var placement) || setup.PlacementFrames.TryGetValue((int)Placement.Default, out placement))
                    placementFrames = placement.AnimFrame.Frames;

                for (var i = 0; i < setup.Parts.Count; i++)
                {
                    var gfxObjId = setup.Parts[i];

                    //if (gfxObjId == 0x010001ec)   // skip anchor locations
                        //continue;

                    if (!gfxObjIdCnts.TryGetValue(gfxObjId, out var gfxObjIdCnt))
                        gfxObjIdCnts[gfxObjId] = 1;
                    else
                        gfxObjIdCnts[gfxObjId] = ++gfxObjIdCnt;

                    var meshNodeName = $"{gfxObjId:X8}";

                    if (gfxObjIdCnt > 1)
                        meshNodeName += "." + gfxObjIdCnt.ToString().PadLeft(3, '0');

                    var meshNode = new Assimp.Node(meshNodeName);

                    var transform = Matrix4x4.Identity;

                    if (i < setup.DefaultScale.Count && setup.DefaultScale[i] != Vector3.One)
                    {
                        transform = Matrix4x4.CreateScale(setup.DefaultScale[i].X, setup.DefaultScale[i].Z, setup.DefaultScale[i].Y);
                    }

                    if (placementFrames != null && i < placementFrames.Count)
                    {
                        var partFrame = placementFrames[i];

                        var rotate = new System.Numerics.Quaternion(-partFrame.Orientation.X, partFrame.Orientation.Z, partFrame.Orientation.Y, partFrame.Orientation.W);
                        var translate = new Vector3(-partFrame.Origin.X, partFrame.Origin.Z, partFrame.Origin.Y);

                        transform *= Matrix4x4.CreateFromQuaternion(rotate) * Matrix4x4.CreateTranslation(translate);
                    }

                    if (transform != Matrix4x4.Identity)
                    {
                        meshNode.Transform = new Assimp.Matrix4x4(
                            transform.M11, transform.M21, transform.M31, transform.M41,
                            transform.M12, transform.M22, transform.M32, transform.M42,
                            transform.M13, transform.M23, transform.M33, transform.M43,
                            transform.M14, transform.M24, transform.M34, transform.M44);
                    }

                    var meshes = ExportGfxObj_Assimp(gfxObjId, materialIdx);

                    foreach (var mesh in meshes)
                    {
                        scene.Meshes.Add(mesh);
                        meshNode.MeshIndices.Add(scene.Meshes.Count - 1);
                    }

                    scene.RootNode.Children.Add(meshNode);
                }
            }
            else
            {
                var meshNode = new Assimp.Node($"{fileID:X8}");

                var meshes = ExportGfxObj_Assimp(fileID, materialIdx);

                foreach (var mesh in meshes)
                {
                    scene.Meshes.Add(mesh);
                    meshNode.MeshIndices.Add(scene.Meshes.Count - 1);
                }

                scene.RootNode.Children.Add(meshNode);
            }

            ExportSurfaces_Assimp(outFilename, scene, materialIdx);

            if (motionData != null)
                BuildAnimation_Assimp(scene, motionData);

            using (var ctx = new AssimpContext())
            {
                var mesh = scene.Meshes[0];

                if (outFilename.EndsWith(".fbx"))
                {
                    if (ctx.ExportFile(scene, outFilename, "fbx"))

                        MainWindow.Instance.AddStatusText($"Wrote {outFilename}");
                    else
                        MainWindow.Instance.AddStatusText($"Failed to export {outFilename}");

                    /*var outTextFilename = outFilename.Replace(".fbx", "-text.fbx");

                    if (ctx.ExportFile(scene, outTextFilename, "fbxa"))
                        MainWindow.Instance.AddStatusText($"Wrote {outTextFilename}");
                    else
                        MainWindow.Instance.AddStatusText($"Failed to export {outTextFilename}");*/

                    FixFBX(outFilename);
                }
                else if (outFilename.EndsWith(".dae"))
                {
                    if (ctx.ExportFile(scene, outFilename, "collada"))
                        MainWindow.Instance.AddStatusText($"Wrote {outFilename}");
                    else
                        MainWindow.Instance.AddStatusText($"Failed to export {outFilename}");
                }
                else
                    return false;
            }
            return true;
        }

        private static List<Assimp.Mesh> ExportGfxObj_Assimp(uint gfxObjID, Dictionary<uint, MaterialIdx> materials)
        {
            // assimp meshes must be split up by material
            // possibly look into adding multiple textures to 1 material?
            var meshes = new Dictionary<uint, Assimp.Mesh>();

            var gfxObj = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.GfxObj>(gfxObjID);

            // vertices
            var vertices = gfxObj.VertexArray.Vertices.OrderBy(i => i.Key).Select(i => i.Value).ToList();

            // normals
            var normals = gfxObj.VertexArray.Vertices.OrderBy(i => i.Key).Select(i => i.Value).ToList();

            // texture coordinates
            var vertexUVs = new Dictionary<VertexUV, Vector2D>();

            for (var i = 0; i < vertices.Count(); i++)
            {
                var v = vertices[i];

                for (var j = 0; j < v.UVs.Count; j++)
                {
                    var uv = v.UVs[j];

                    vertexUVs.Add(new VertexUV(i, j), new Vector2D(uv.U, uv.V));
                }
            }

            // polygons
            foreach (var poly in gfxObj.Polygons.OrderBy(i => i.Key).Select(i => i.Value))
            {
                var currentSurfaceId = gfxObj.Surfaces[poly.PosSurface];

                if (!meshes.TryGetValue(currentSurfaceId, out var mesh))
                {
                    mesh = new Assimp.Mesh();
                    if (!materials.TryGetValue(currentSurfaceId, out var materialIdx))
                    {
                        materialIdx = new MaterialIdx(materials.Count);
                        materials.Add(currentSurfaceId, materialIdx);
                    }
                    mesh.MaterialIndex = materialIdx.MaterialId;
                    mesh.UVComponentCount[0] = 2;
                    meshes.Add(currentSurfaceId, mesh);
                }

                var face = new Assimp.Face();

                for (var i = 0; i < poly.VertexIds.Count; i++)
                {
                    var origVertIdx = poly.VertexIds[i];
                    var v = vertices[origVertIdx];
                    var n = normals[origVertIdx];
                    var uv = vertexUVs[new VertexUV(origVertIdx, i < poly.PosUVIndices.Count ? poly.PosUVIndices[i] : 0)];     // investigate: some polys dont have uv arrays?

                    // denormalize for assimp :(
                    mesh.Vertices.Add(new Vector3D(-v.Origin.X, v.Origin.Z, v.Origin.Y));
                    face.Indices.Add(mesh.Vertices.Count - 1);
                    mesh.Normals.Add(new Vector3D(-n.Normal.X, n.Normal.Z, n.Normal.Y));
                    mesh.TextureCoordinateChannels[0].Add(new Vector3D(uv.X, -uv.Y, 0.0f));
                }

                mesh.Faces.Add(face);
            }

            return meshes.Values.ToList();
        }

        private static void ExportSurfaces_Assimp(string outFilename, Assimp.Scene scene, Dictionary<uint, MaterialIdx> materialIdx)
        {
            var fi = new System.IO.FileInfo(outFilename);

            foreach (var kvp in materialIdx.OrderBy(i => i.Value.MaterialId))
            {
                var surfaceID = kvp.Key;
                var materialId = kvp.Value.MaterialId;

                var surfaceFilename = fi.DirectoryName + Path.DirectorySeparatorChar + $"{surfaceID:X8}.png";

                if (!File.Exists(surfaceFilename))
                    ExportImage(surfaceID, surfaceFilename);

                var material = new Assimp.Material();
                material.Name = $"{surfaceID:X8}";
                material.TextureDiffuse = new Assimp.TextureSlot()
                {
                    //FilePath = surfaceFilename,
                    FilePath = $"{surfaceID:X8}.png",
                    TextureType = TextureType.Diffuse,
                    //WrapModeU = TextureWrapMode.Wrap,
                    //WrapModeV = TextureWrapMode.Wrap,
                };

                // if this is 0, assimp defaults to lambert shading
                // even forcing phong shading seems to be ignored. this must be set...
                material.Shininess = 0.00001f;

                // there seems to be no other way to set some important material props via assimp currently :(
                // going to use this as a base, and then fill in the rest via raw fbx reading/writing via UkooLabs.FbxSharpie

                scene.Materials.Add(material);
            }
        }

        private static void BuildAnimation_Assimp(Assimp.Scene scene, MotionData motionData)
        {
            var animation = new Assimp.Animation();
            animation.Name = "ACAnim";

            foreach (var animData in motionData.Anims)
            {
                var perFrame = 1.0f / animData.Framerate;

                var anim = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.Animation>(animData.AnimId);

                animation.DurationInTicks = perFrame * anim.PartFrames.Count;
                animation.TicksPerSecond = 1.0;

                for (var animFrameIdx = 0; animFrameIdx < anim.PartFrames.Count; animFrameIdx++)
                {
                    var curTime = perFrame * animFrameIdx;
                    var partFrames = anim.PartFrames[animFrameIdx];

                    for (var partIdx = 0; partIdx < partFrames.Frames.Count; partIdx++)
                    {
                        var partFrame = partFrames.Frames[partIdx];
                        var meshNode = scene.RootNode.Children[partIdx];

                        NodeAnimationChannel nodeAnimationChannel = null;

                        if (partIdx < animation.NodeAnimationChannels.Count)
                        {
                            nodeAnimationChannel = animation.NodeAnimationChannels[partIdx];
                        }
                        else
                        {
                            nodeAnimationChannel = new NodeAnimationChannel();
                            nodeAnimationChannel.NodeName = meshNode.Name;

                            animation.NodeAnimationChannels.Add(nodeAnimationChannel);
                        }

                        nodeAnimationChannel.PositionKeys.Add(new VectorKey(curTime, new Vector3D(-partFrame.Origin.X, partFrame.Origin.Z, partFrame.Origin.Y)));
                        nodeAnimationChannel.RotationKeys.Add(new QuaternionKey(curTime, new Assimp.Quaternion(partFrame.Orientation.W, -partFrame.Orientation.X, partFrame.Orientation.Z, partFrame.Orientation.Y)));
                    }
                }
            }
            scene.Animations.Add(animation);

            // multiple anims "work", but managing them in blender feels very unruly atm...
            // only doing single animations for now, until this is figured out better
        }

        private static void FixFBX(string filename)
        {
            var fbx = FbxIO.Read(filename);

            ShowNodes(fbx.Nodes, null);

            FbxIO.WriteBinary(fbx, filename);
        }

        private static readonly bool Debug = false;

        private static void ShowNodes(FbxNode[] nodes, FbxNode parent, int level = 0)
        {
            var prefix = "".PadLeft(level * 2, ' ');

            foreach (var node in nodes)
            {
                if (node == null) continue;

                if (Debug) Console.WriteLine(prefix + node.Identifier.Value);

                if (node.Properties.Length > 0)
                {
                    //Console.WriteLine($"Properties:");
                    foreach (var prop in node.Properties)
                    {
                        if (prop is StringToken st)
                        {
                            if (Debug) Console.WriteLine($"{prefix} - {st.Value}");

                            if (st.Value == "UnitScaleFactor")
                            {
                                if (node.Properties.LastOrDefault() is DoubleToken unitScaleFactor)
                                {
                                    //Console.WriteLine($"Fixed UnitScaleFactor {unitScaleFactor.Value} -> 100");
                                    unitScaleFactor.Value = 100.0;
                                }
                                else
                                    Console.WriteLine($"Found UnitScaleFactor, but not fixed!");
                            }
                            else if (st.Value == "Shininess")
                            {
                                if (node.Properties.LastOrDefault() is DoubleToken shininess)
                                {
                                    //Console.WriteLine($"Fixed Shininess {shininess.Value} -> 0");
                                    shininess.Value = 0.0;
                                }
                                else
                                    Console.WriteLine($"Found Shininess, but not fixed!");

                                var specularFactor = new FbxNode(new IdentifierToken("P"));
                                specularFactor.AddProperty(new StringToken("SpecularFactor"));
                                specularFactor.AddProperty(new StringToken("Number"));
                                specularFactor.AddProperty(new StringToken(""));
                                specularFactor.AddProperty(new StringToken("A"));
                                specularFactor.AddProperty(new DoubleToken(0));
                                parent.AddNode(specularFactor);

                                var reflectionFactor = new FbxNode(new IdentifierToken("P"));
                                reflectionFactor.AddProperty(new StringToken("ReflectionFactor"));
                                reflectionFactor.AddProperty(new StringToken("Number"));
                                reflectionFactor.AddProperty(new StringToken(""));
                                reflectionFactor.AddProperty(new StringToken("A"));
                                reflectionFactor.AddProperty(new DoubleToken(0));
                                parent.AddNode(reflectionFactor);
                            }
                            else if (st.Value == "ShininessExponent")
                            {
                                if (node.Properties.LastOrDefault() is DoubleToken shininessExponent)
                                {
                                    if (shininessExponent.Value < 20)
                                    {
                                        //Console.WriteLine($"Fixed ShininessExponent {shininessExponent.Value} -> 0");
                                        shininessExponent.Value = 0.0;
                                    }
                                }
                                else
                                    Console.WriteLine($"Found ShininessExponent, but not fixed!");
                            }
                        }
                        else if (prop is IntegerToken it)
                        {
                            if (Debug) Console.WriteLine($"{prefix} - {it.Value}");
                        }
                        else if (prop is FloatToken ft)
                        {
                            if (Debug) Console.WriteLine($"{prefix} - {ft.Value}");
                        }
                        else if (prop is BooleanToken bt)
                        {
                            if (Debug) Console.WriteLine($"{prefix} - {bt.Value}");
                        }
                        else if (prop is DoubleToken dt)
                        {
                            if (Debug) Console.WriteLine($"{prefix} - {dt.Value}");
                        }
                        else if (prop is LongToken lt)
                        {
                            if (Debug) Console.WriteLine($"{prefix} - {lt.Value}");
                        }
                        else if (prop is ShortToken ht)
                        {
                            if (Debug) Console.WriteLine($"{prefix} - {ht.Value}");
                        }
                    }
                }

                if (node.Nodes.Length > 0)
                    ShowNodes(node.Nodes, node, level + 1);
            }
        }
        // ===============================
        // VWorlds .X Export
        // ===============================

        /// <summary>
        /// Exports a GfxObj (0x01) or Setup (0x02) model as a DirectX text .X file.
        /// </summary>
        public static bool ExportGfxObjOrSetup_X(uint fileID, string outFilename)
        {
            if (fileID >> 24 != 0x1 && fileID >> 24 != 0x2)
            {
                Console.WriteLine($"Unknown model file: {fileID:X8}");
                return false;
            }

            var fi = new System.IO.FileInfo(outFilename);
            var outDir = fi.DirectoryName;

            var sb = new StringBuilder();
            sb.AppendLine("xof 0302txt 0064");
            sb.AppendLine();
            sb.AppendLine($"// Exported from Asheron's Call {fileID:X8}");
            sb.AppendLine();

            var isSetup = fileID >> 24 == 0x2;

            if (isSetup)
            {
                sb.AppendLine($"Frame Setup_{fileID:X8} {{");
                WriteFrameTransformMatrix(sb, Matrix4x4.Identity, "  ");
                WriteSetupMesh(sb, fileID, outDir, "  ");
                sb.AppendLine("}");
            }
            else
            {
                sb.AppendLine($"Frame GfxObj_{fileID:X8} {{");
                WriteFrameTransformMatrix(sb, Matrix4x4.Identity, "  ");
                WriteGfxObjMesh_X(sb, fileID, Matrix4x4.Identity, outDir, $"Obj_{fileID:X8}", "  ");
                sb.AppendLine("}");
            }

            File.WriteAllText(outFilename, sb.ToString());
            MainWindow.Instance.AddStatusText($"Wrote {outFilename}");
            return true;
        }

        /// <summary>
        /// Exports an EnvCell (dungeon/interior room) as a DirectX text .X file
        /// with textures exported as GIF for VWorlds D3DRM compatibility.
        /// </summary>
        // ===============================
        // VWorlds Actor Export (Bone Hierarchy)
        // ===============================

        /// <summary>
        /// Exports an AC Setup (0x02) as a VWorlds Actor-compatible hierarchical .x file
        /// with named Frames for each bone, suitable for CJoint bone animation.
        /// </summary>
        public static bool ExportActor_X(uint setupID, string outFilename)
        {
            if (setupID >> 24 != 0x2)
            {
                Console.WriteLine($"Not a Setup file: {setupID:X8}");
                return false;
            }

            var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(setupID);
            if (setup == null || setup.Parts.Count == 0)
            {
                Console.WriteLine($"Failed to load Setup {setupID:X8}");
                return false;
            }

            var fi = new System.IO.FileInfo(outFilename);
            var outDir = fi.DirectoryName;

            // Get rest pose placement frames
            List<ACE.DatLoader.Entity.Frame> placementFrames = null;
            if (setup.PlacementFrames.TryGetValue((int)Placement.Resting, out var placement) ||
                setup.PlacementFrames.TryGetValue((int)Placement.Default, out placement))
                placementFrames = placement.AnimFrame.Frames;

            // Build parent-child tree from flat ParentIndex list
            var hasHierarchy = setup.ParentIndex.Count == setup.Parts.Count;
            var children = new Dictionary<int, List<int>>();
            var roots = new List<int>();

            if (hasHierarchy)
            {
                // Debug: log parent indices
                var parentDebug = new StringBuilder();
                parentDebug.Append("ParentIndex: ");
                for (var i = 0; i < setup.ParentIndex.Count; i++)
                    parentDebug.Append($"[{i}]={setup.ParentIndex[i]} ");
                Console.WriteLine(parentDebug.ToString());

                for (var i = 0; i < setup.Parts.Count; i++)
                {
                    var parentIdx = (int)setup.ParentIndex[i];
                    // Root bone: parent index is 0xFFFFFFFF, equals self, or is out of range
                    // Also treat index 0 as root if its parent is 0 (self-referencing root)
                    if (parentIdx == i || parentIdx < 0 || (uint)parentIdx >= setup.Parts.Count)
                    {
                        roots.Add(i);
                    }
                    else
                    {
                        if (!children.ContainsKey(parentIdx))
                            children[parentIdx] = new List<int>();
                        children[parentIdx].Add(i);
                    }
                }

                // If no roots found, bone 0 is the root
                if (roots.Count == 0)
                    roots.Add(0);

                Console.WriteLine($"Roots: {string.Join(", ", roots)}");
                foreach (var kvp in children)
                    Console.WriteLine($"  Bone {kvp.Key} children: {string.Join(", ", kvp.Value)}");
            }
            else
            {
                // No hierarchy - all parts are roots
                for (var i = 0; i < setup.Parts.Count; i++)
                    roots.Add(i);
            }

            var sb = new StringBuilder();
            sb.AppendLine("xof 0302txt 0064");
            sb.AppendLine();
            sb.AppendLine($"// AC Actor Export: Setup {setupID:X8}");
            sb.AppendLine($"// Parts: {setup.Parts.Count}, Hierarchy: {hasHierarchy}");
            sb.AppendLine();

            // Write root frame
            sb.AppendLine("Frame Root {");
            sb.AppendLine("  FrameTransformMatrix {");
            sb.AppendLine("    1.0, 0.0, 0.0, 0.0,");
            sb.AppendLine("    0.0, 1.0, 0.0, 0.0,");
            sb.AppendLine("    0.0, 0.0, 1.0, 0.0,");
            sb.AppendLine("    0.0, 0.0, 0.0, 1.0;;");
            sb.AppendLine("  }");

            // Recursively write bone hierarchy
            foreach (var rootIdx in roots)
                WriteActorBone(sb, setup, rootIdx, placementFrames, children, outDir, "  ");

            sb.AppendLine("}");

            File.WriteAllText(outFilename, sb.ToString());
            MainWindow.Instance.AddStatusText($"Wrote actor {outFilename} ({setup.Parts.Count} bones)");

            // Generate actor creation script
            var vbsFilename = Path.Combine(outDir, $"{setupID:X8}_actor.vbs");
            GenerateActorScript(setupID, setup, outFilename, vbsFilename);

            return true;
        }

        /// <summary>
        /// Gets a human-readable bone name based on index and part count.
        /// </summary>
        private static string GetBoneName(int boneIdx, int totalBones)
        {
            // Common AC creature bone naming by index
            // These are rough mappings - AC doesn't store bone names, just indices
            if (totalBones >= 6)
            {
                switch (boneIdx)
                {
                    case 0: return "Torso";
                    case 1: return "Head";
                    case 2: return "LeftUpperArm";
                    case 3: return "LeftForearm";
                    case 4: return "RightUpperArm";
                    case 5: return "RightForearm";
                    case 6: return "LeftUpperLeg";
                    case 7: return "LeftLowerLeg";
                    case 8: return "LeftFoot";
                    case 9: return "RightUpperLeg";
                    case 10: return "RightLowerLeg";
                    case 11: return "RightFoot";
                    case 12: return "LeftHand";
                    case 13: return "RightHand";
                    case 14: return "Pelvis";
                    case 15: return "Abdomen";
                    default: return $"Bone_{boneIdx}";
                }
            }
            return $"Bone_{boneIdx}";
        }

        /// <summary>
        /// Recursively writes a bone frame with its mesh and child bones.
        /// </summary>
        private static void WriteActorBone(StringBuilder sb, SetupModel setup, int boneIdx,
            List<ACE.DatLoader.Entity.Frame> placementFrames, Dictionary<int, List<int>> children,
            string outDir, string indent)
        {
            var partId = setup.Parts[boneIdx];
            var boneName = GetBoneName(boneIdx, setup.Parts.Count);

            // Get bone rest position (local offset from parent)
            var origin = Vector3.Zero;
            var orientation = System.Numerics.Quaternion.Identity;

            if (placementFrames != null && boneIdx < placementFrames.Count)
            {
                origin = placementFrames[boneIdx].Origin;
                orientation = placementFrames[boneIdx].Orientation;
            }

            // Build local transform matrix
            // Swizzle AC Z-up to VWorlds Y-up
            var vwOrigin = ACtoVW(origin);
            var rotMatrix = Matrix4x4.CreateFromQuaternion(orientation);

            sb.AppendLine($"{indent}// Bone {boneIdx}: {boneName} (GfxObj {partId:X8})");
            sb.AppendLine($"{indent}Frame {boneName} {{");

            // Write FrameTransformMatrix with bone offset (translation only for rest pose,
            // rotation handled by baking into mesh vertices)
            sb.AppendLine($"{indent}  FrameTransformMatrix {{");
            sb.AppendLine($"{indent}    1.0, 0.0, 0.0, 0.0,");
            sb.AppendLine($"{indent}    0.0, 1.0, 0.0, 0.0,");
            sb.AppendLine($"{indent}    0.0, 0.0, 1.0, 0.0,");
            sb.AppendLine($"{indent}    {F(vwOrigin.X)}, {F(vwOrigin.Y)}, {F(vwOrigin.Z)}, 1.0;;");
            sb.AppendLine($"{indent}  }}");

            // Write mesh for this bone (skip anchor parts)
            if (partId != 0x010001ec)
            {
                // Get scale for this part
                var scale = Matrix4x4.Identity;
                if (boneIdx < setup.DefaultScale.Count && setup.DefaultScale[boneIdx] != Vector3.One)
                    scale = Matrix4x4.CreateScale(setup.DefaultScale[boneIdx]);

                // Bake orientation rotation into mesh vertices (so bone rest pose looks correct)
                var meshTransform = scale * rotMatrix;

                WriteGfxObjMesh_X(sb, partId, meshTransform, outDir,
                    $"{boneName}Mesh", indent + "  ");
            }

            // Recurse into child bones
            if (children.TryGetValue(boneIdx, out var childList))
            {
                foreach (var childIdx in childList)
                    WriteActorBone(sb, setup, childIdx, placementFrames, children, outDir, indent + "  ");
            }

            sb.AppendLine($"{indent}}}");
        }

        /// <summary>
        /// Generates a VBS script for creating a VWorlds Actor from the exported .x file.
        /// </summary>
        private static void GenerateActorScript(uint setupID, SetupModel setup, string xFilename, string vbsFilename)
        {
            var fi = new System.IO.FileInfo(xFilename);
            var xName = fi.Name;

            var sb = new StringBuilder();
            sb.AppendLine($"' VWorlds Actor creation script for AC Setup {setupID:X8}");
            sb.AppendLine($"' {setup.Parts.Count} bones");
            sb.AppendLine();
            sb.AppendLine("' Assumes World and Room are already set up");
            sb.AppendLine();
            sb.AppendLine("Dim actor");
            sb.AppendLine($"Set actor = World.CreateInstance(\"AC_{setupID:X8}\", World.Exemplar(\"Actor\"))");
            sb.AppendLine("actor.MoveInto Room");
            sb.AppendLine($"actor.InitializeGraphics \"{xName}\", 0.0, 0.0, 0.0, 0.0, 0.0, 1.0");
            sb.AppendLine();
            sb.AppendLine("' Joint names available for SetJointRotation:");
            for (var i = 0; i < setup.Parts.Count; i++)
            {
                var name = GetBoneName(i, setup.Parts.Count);
                var parentIdx = i < setup.ParentIndex.Count ? (int)setup.ParentIndex[i] : -1;
                var parentName = parentIdx >= 0 && parentIdx != i ? GetBoneName(parentIdx, setup.Parts.Count) : "Root";
                sb.AppendLine($"'   {name} (bone {i}, parent: {parentName})");
            }
            sb.AppendLine();
            sb.AppendLine("' Example: wave gesture");
            sb.AppendLine("' For i = 0 To 30");
            sb.AppendLine("'     angle = Sin(i * 0.2) * 1.5");
            sb.AppendLine($"'     actor.SetJointRotation \"{GetBoneName(4, setup.Parts.Count)}\", angle");
            sb.AppendLine("'     WScript.Sleep 33");
            sb.AppendLine("' Next");

            File.WriteAllText(vbsFilename, sb.ToString());
            MainWindow.Instance.AddStatusText($"Wrote {vbsFilename}");
        }

        /// <summary>
        /// Exports an AC Setup (0x02) as a single flat .x file — all parts merged into one mesh
        /// with rest pose transforms baked into vertices. No bone hierarchy. Works as a static Artifact.
        /// </summary>
        public static bool ExportActorStatic_X(uint setupID, string outFilename)
        {
            if (setupID >> 24 != 0x2)
            {
                Console.WriteLine($"Not a Setup file: {setupID:X8}");
                return false;
            }

            var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(setupID);
            if (setup == null || setup.Parts.Count == 0)
            {
                Console.WriteLine($"Failed to load Setup {setupID:X8}");
                return false;
            }

            var fi = new System.IO.FileInfo(outFilename);
            var outDir = fi.DirectoryName;

            // Get rest pose placement frames
            List<ACE.DatLoader.Entity.Frame> placementFrames = null;
            if (setup.PlacementFrames.TryGetValue((int)Placement.Resting, out var placement) ||
                setup.PlacementFrames.TryGetValue((int)Placement.Default, out placement))
                placementFrames = placement.AnimFrame.Frames;

            // Compute world-space transform for each bone using AC's frame combine convention:
            //   world.Origin = parent.Origin + Vector3.Transform(child.Origin, parent.Orientation)
            //   world.Orientation = parent.Orientation * child.Orientation
            var hasHierarchy = setup.ParentIndex.Count == setup.Parts.Count;
            var worldOrigins = new Vector3[setup.Parts.Count];
            var worldOrients = new System.Numerics.Quaternion[setup.Parts.Count];
            var worldComputed = new bool[setup.Parts.Count];

            // First pass: find the true root by detecting circular parent chains
            // Walk from bone 0 and find the cycle
            var trueRoot = 0;
            if (hasHierarchy)
            {
                // Find bone with lowest local Z (closest to ground) in a cycle, or self-referencing
                for (var i = 0; i < setup.Parts.Count; i++)
                {
                    var pi = (int)setup.ParentIndex[i];
                    if (pi == i || pi < 0 || (uint)pi >= setup.Parts.Count)
                    {
                        trueRoot = i;
                        break;
                    }
                }

                // If no self-referencing root, detect cycle and pick the bone with lowest Z origin
                if ((int)setup.ParentIndex[trueRoot] != trueRoot)
                {
                    // Walk from bone 0, find cycle members
                    var visited = new HashSet<int>();
                    var cur = 0;
                    while (!visited.Contains(cur) && cur >= 0 && (uint)cur < setup.Parts.Count)
                    {
                        visited.Add(cur);
                        cur = (int)setup.ParentIndex[cur];
                    }
                    // cur is now the start of the cycle - find member with lowest Z (ground level)
                    if (cur >= 0 && (uint)cur < setup.Parts.Count)
                    {
                        var cycleStart = cur;
                        var bestZ = float.MaxValue;
                        trueRoot = cur;
                        do
                        {
                            var z = (placementFrames != null && cur < placementFrames.Count) ? placementFrames[cur].Origin.Z : 0;
                            if (z < bestZ) { bestZ = z; trueRoot = cur; }
                            cur = (int)setup.ParentIndex[cur];
                        } while (cur != cycleStart);
                    }
                }
                MainWindow.Instance.AddStatusText($"True root bone: {trueRoot}");
            }

            // Recursive function to compute world transform for a bone
            void ComputeWorldTransform(int idx)
            {
                if (worldComputed[idx]) return;
                worldComputed[idx] = true;

                var localOrigin = Vector3.Zero;
                var localOrient = System.Numerics.Quaternion.Identity;

                if (placementFrames != null && idx < placementFrames.Count)
                {
                    localOrigin = placementFrames[idx].Origin;
                    localOrient = placementFrames[idx].Orientation;
                }

                if (!hasHierarchy)
                {
                    worldOrigins[idx] = localOrigin;
                    worldOrients[idx] = localOrient;
                    return;
                }

                var parentIdx = (int)setup.ParentIndex[idx];

                // Root bone or cycle break point: use local transform as world
                if (idx == trueRoot || parentIdx == idx || parentIdx < 0 || (uint)parentIdx >= setup.Parts.Count)
                {
                    worldOrigins[idx] = localOrigin;
                    worldOrients[idx] = localOrient;
                    return;
                }

                // Ensure parent is computed first
                ComputeWorldTransform(parentIdx);

                // AC frame combine: parent.Origin + rotate(child.Origin, parent.Orientation)
                worldOrigins[idx] = worldOrigins[parentIdx] + Vector3.Transform(localOrigin, worldOrients[parentIdx]);
                worldOrients[idx] = System.Numerics.Quaternion.Multiply(worldOrients[parentIdx], localOrient);
            }

            // PlacementFrames appear to store ABSOLUTE model-space positions, not relative offsets.
            // Just use local transforms directly as world transforms.
            for (var i = 0; i < setup.Parts.Count; i++)
            {
                var localOrigin = Vector3.Zero;
                var localOrient = System.Numerics.Quaternion.Identity;

                if (placementFrames != null && i < placementFrames.Count)
                {
                    localOrigin = placementFrames[i].Origin;
                    localOrient = placementFrames[i].Orientation;
                }

                worldOrigins[i] = localOrigin;
                worldOrients[i] = localOrient;
            }

            // Debug: print bone world positions
            for (var i = 0; i < setup.Parts.Count; i++)
            {
                var parentIdx = hasHierarchy && i < setup.ParentIndex.Count ? (int)setup.ParentIndex[i] : -1;
                var localOrigin = (placementFrames != null && i < placementFrames.Count) ? placementFrames[i].Origin : Vector3.Zero;
                MainWindow.Instance.AddStatusText($"Bone {i}: local=({localOrigin.X:F3},{localOrigin.Y:F3},{localOrigin.Z:F3}) world=({worldOrigins[i].X:F3},{worldOrigins[i].Y:F3},{worldOrigins[i].Z:F3}) parent={parentIdx}");
            }

            // Build world transform matrices from computed origins and orientations
            var worldTransforms = new Matrix4x4[setup.Parts.Count];
            for (var i = 0; i < setup.Parts.Count; i++)
                worldTransforms[i] = Matrix4x4.CreateFromQuaternion(worldOrients[i]) * Matrix4x4.CreateTranslation(worldOrigins[i]);

            // Now merge all parts into one big mesh
            var allVerts = new List<Vector3>();
            var allNorms = new List<Vector3>();
            var allUVs = new List<Vector2>();
            var allFaces = new List<int[]>();
            var allFaceMats = new List<int>();
            var allMaterialNames = new List<string>();
            var allTextureFilenames = new List<string>();
            var materialRemap = new Dictionary<uint, int>(); // surfaceID → global material index

            for (var partIdx = 0; partIdx < setup.Parts.Count; partIdx++)
            {
                var partId = setup.Parts[partIdx];
                if (partId == 0x010001ec) continue; // skip anchor

                var gfxObj = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.GfxObj>(partId);
                if (gfxObj == null) continue;

                var vertices = gfxObj.VertexArray.Vertices.OrderBy(k => k.Key).Select(k => k.Value).ToList();

                // Compute full transform: part scale + world bone transform
                var transform = Matrix4x4.Identity;
                if (partIdx < setup.DefaultScale.Count && setup.DefaultScale[partIdx] != Vector3.One)
                    transform = Matrix4x4.CreateScale(setup.DefaultScale[partIdx]);

                transform = transform * worldTransforms[partIdx];

                var baseVertex = allVerts.Count;

                // Build surface → material index mapping for this part
                var partSurfaceRemap = new Dictionary<int, int>();
                foreach (var poly in gfxObj.Polygons.OrderBy(k => k.Key).Select(k => k.Value))
                {
                    if (poly.Stippling.HasFlag(StipplingType.NoPos)) continue;
                    var surfIdx = poly.PosSurface;
                    if (partSurfaceRemap.ContainsKey(surfIdx)) continue;

                    if (surfIdx < gfxObj.Surfaces.Count)
                    {
                        var surfaceID = gfxObj.Surfaces[surfIdx];
                        if (!materialRemap.TryGetValue(surfaceID, out var globalMatIdx))
                        {
                            globalMatIdx = allMaterialNames.Count;
                            materialRemap[surfaceID] = globalMatIdx;
                            allMaterialNames.Add($"Mat_{surfaceID:X8}");
                            var texName = $"{surfaceID:X8}.gif";
                            allTextureFilenames.Add(texName);
                            var texPath = Path.Combine(outDir, texName);
                            if (!File.Exists(texPath))
                                ExportImageAsGif(surfaceID, texPath);
                        }
                        partSurfaceRemap[surfIdx] = globalMatIdx;
                    }
                    else
                    {
                        partSurfaceRemap[surfIdx] = 0;
                    }
                }

                // Emit vertices
                var vertexMap = new Dictionary<long, int>();
                foreach (var poly in gfxObj.Polygons.OrderBy(k => k.Key).Select(k => k.Value))
                {
                    if (poly.Stippling.HasFlag(StipplingType.NoPos)) continue;

                    var firstIdx = -1;
                    var prevIdx = -1;

                    for (var i = 0; i < poly.VertexIds.Count; i++)
                    {
                        var vid = poly.VertexIds[i];
                        var uvIdx = i < poly.PosUVIndices.Count ? poly.PosUVIndices[i] : (byte)0;
                        long key = ((long)(partIdx * 10000 + vid) << 16) | uvIdx;

                        if (!vertexMap.TryGetValue(key, out var xIdx))
                        {
                            xIdx = allVerts.Count;
                            vertexMap[key] = xIdx;

                            var sv = vertices[vid];
                            allVerts.Add(ACtoVW(Vector3.Transform(sv.Origin, transform)));
                            allNorms.Add(ACtoVW(Vector3.TransformNormal(sv.Normal, transform)));

                            if (sv.UVs != null && uvIdx < sv.UVs.Count)
                                allUVs.Add(new Vector2(sv.UVs[uvIdx].U, sv.UVs[uvIdx].V));
                            else
                                allUVs.Add(Vector2.Zero);
                        }

                        if (i == 0) firstIdx = xIdx;
                        else if (i >= 2)
                        {
                            allFaces.Add(new int[] { firstIdx, xIdx, prevIdx }); // reversed winding
                            allFaceMats.Add(partSurfaceRemap.ContainsKey(poly.PosSurface) ? partSurfaceRemap[poly.PosSurface] : 0);
                        }
                        prevIdx = xIdx;
                    }
                }
            }

            // Center the model: X/Z centered, Y (up) shifted so feet are at Y=0
            if (allVerts.Count > 0)
            {
                var minY = float.MaxValue;
                var centerX = 0f;
                var centerZ = 0f;

                foreach (var v in allVerts)
                {
                    centerX += v.X;
                    centerZ += v.Z;
                    if (v.Y < minY) minY = v.Y;
                }
                centerX /= allVerts.Count;
                centerZ /= allVerts.Count;

                // Shift so model is centered on X/Z and feet touch Y=0
                var offset = new Vector3(centerX, minY, centerZ);
                for (var i = 0; i < allVerts.Count; i++)
                    allVerts[i] = allVerts[i] - offset;
            }

            // Write the merged .x file
            var sb = new StringBuilder();
            sb.AppendLine("xof 0302txt 0064");
            sb.AppendLine();
            sb.AppendLine($"// AC Static Model: Setup {setupID:X8}");
            sb.AppendLine($"// {setup.Parts.Count} parts merged into one mesh, centered at origin");
            sb.AppendLine();

            sb.AppendLine($"Frame Model_{setupID:X8} {{");
            sb.AppendLine("  FrameTransformMatrix {");
            sb.AppendLine("    1.0, 0.0, 0.0, 0.0,");
            sb.AppendLine("    0.0, 1.0, 0.0, 0.0,");
            sb.AppendLine("    0.0, 0.0, 1.0, 0.0,");
            sb.AppendLine("    0.0, 0.0, 0.0, 1.0;;");
            sb.AppendLine("  }");

            WriteXMesh(sb, $"Mesh_{setupID:X8}", allVerts, allNorms, allUVs, allFaces, allFaceMats, allMaterialNames, allTextureFilenames, "  ");

            sb.AppendLine("}");

            File.WriteAllText(outFilename, sb.ToString());
            MainWindow.Instance.AddStatusText($"Wrote static model {outFilename} ({allVerts.Count} verts, {allFaces.Count} tris, {allMaterialNames.Count} materials)");
            return true;
        }

        public static bool ExportEnvCell_X(uint envCellID, string outFilename)
        {
            var envCell = DatManager.CellDat.ReadFromDat<ACE.DatLoader.FileTypes.EnvCell>(envCellID);
            if (envCell == null || envCell.EnvironmentId == 0)
            {
                Console.WriteLine($"Failed to load EnvCell {envCellID:X8}");
                return false;
            }

            var environment = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.Environment>(envCell.EnvironmentId);
            if (environment == null || !environment.Cells.TryGetValue(envCell.CellStructure, out var cellStruct))
            {
                Console.WriteLine($"Failed to load Environment {envCell.EnvironmentId:X8} CellStructure {envCell.CellStructure}");
                return false;
            }

            var fi = new System.IO.FileInfo(outFilename);
            var outDir = fi.DirectoryName;

            var sb = new StringBuilder();
            sb.AppendLine("xof 0302txt 0064");
            sb.AppendLine();
            sb.AppendLine($"// Exported from Asheron's Call EnvCell {envCellID:X8}");
            sb.AppendLine($"// Environment {envCell.EnvironmentId:X8}, CellStructure {envCell.CellStructure}");
            sb.AppendLine();

            // Build the cell transform from EnvCell.Position
            var cellFrame = envCell.Position;
            var cellMatrix = Matrix4x4.CreateFromQuaternion(cellFrame.Orientation) * Matrix4x4.CreateTranslation(cellFrame.Origin);

            // Write the room geometry as a single Frame
            sb.AppendLine($"Frame EnvCell_{envCellID:X8} {{");
            WriteFrameTransformMatrix(sb, cellMatrix, "  ");

            WriteCellStructMesh(sb, cellStruct, envCell.Surfaces, $"Cell_{envCellID:X8}", outDir, "  ");

            sb.AppendLine("}");

            // Write static objects as separate top-level frames
            for (var i = 0; i < envCell.StaticObjects.Count; i++)
            {
                var stab = envCell.StaticObjects[i];
                var stabFrame = stab.Frame;
                var stabMatrix = Matrix4x4.CreateFromQuaternion(stabFrame.Orientation) * Matrix4x4.CreateTranslation(stabFrame.Origin);

                // Combine cell transform with stab local transform
                var worldMatrix = stabMatrix * cellMatrix;

                var isSetup = stab.Id >> 24 == 0x2;
                var isGfxObj = stab.Id >> 24 == 0x1;

                if (!isSetup && !isGfxObj) continue;

                sb.AppendLine();
                sb.AppendLine($"Frame StaticObj_{i}_{stab.Id:X8} {{");
                WriteFrameTransformMatrix(sb, worldMatrix, "  ");

                if (isSetup)
                    WriteSetupMesh(sb, stab.Id, outDir, "  ");
                else
                    WriteGfxObjMesh_X(sb, stab.Id, Matrix4x4.Identity, outDir, $"Obj_{stab.Id:X8}", "  ");

                sb.AppendLine("}");
            }

            File.WriteAllText(outFilename, sb.ToString());
            MainWindow.Instance.AddStatusText($"Wrote {outFilename}");

            // Generate VBS creation script
            var vbsFilename = Path.Combine(outDir, $"{envCellID:X8}_create.vbs");
            GenerateVWorldsScript(envCellID, outFilename, vbsFilename);

            return true;
        }

        /// <summary>
        /// Exports ALL EnvCells in a landblock as individual .X files (one per cell, one per static object),
        /// plus a VBS script that assembles them into a VWorlds room.
        /// Rotation is baked into the mesh vertices; position is handled by the VBS script.
        /// </summary>
        public static bool ExportDungeon_X(uint anyEnvCellID, string outFilename)
        {
            var landblockID = anyEnvCellID & 0xFFFF0000;
            var landblockInfoID = landblockID | 0xFFFE;

            var landblockInfo = DatManager.CellDat.ReadFromDat<ACE.DatLoader.FileTypes.LandblockInfo>(landblockInfoID);
            if (landblockInfo == null || landblockInfo.NumCells == 0)
            {
                Console.WriteLine($"No EnvCells found in landblock {landblockID:X8}");
                return false;
            }

            var fi = new System.IO.FileInfo(outFilename);
            var outDir = fi.DirectoryName;
            var worldName = $"Dungeon_{landblockID >> 16:X4}";

            // Collect all artifacts for the VBS script
            var artifacts = new List<DungeonArtifact>();
            var totalFiles = 0;

            for (uint i = 0; i < landblockInfo.NumCells; i++)
            {
                var envCellID = landblockID | (0x100 + i);

                ACE.DatLoader.FileTypes.EnvCell envCell = null;
                try { envCell = DatManager.CellDat.ReadFromDat<ACE.DatLoader.FileTypes.EnvCell>(envCellID); }
                catch { continue; }

                if (envCell == null || envCell.EnvironmentId == 0) continue;

                ACE.DatLoader.FileTypes.Environment environment = null;
                try { environment = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.Environment>(envCell.EnvironmentId); }
                catch { continue; }

                if (environment == null || !environment.Cells.TryGetValue(envCell.CellStructure, out var cellStruct))
                    continue;

                // Export cell geometry as its own .x file
                // Bake rotation into vertices, extract position for VBS placement
                var cellFrame = envCell.Position;
                var rotMatrix = Matrix4x4.CreateFromQuaternion(cellFrame.Orientation);
                var position = cellFrame.Origin;

                var cellXName = $"cell_{envCellID:X8}.x";
                var cellXPath = Path.Combine(outDir, cellXName);

                WriteSingleXFile(cellXPath, $"Cell_{envCellID:X8}",
                    cellStruct, envCell.Surfaces, rotMatrix, outDir);

                artifacts.Add(new DungeonArtifact($"Cell_{i:D3}", cellXName, position));
                totalFiles++;

                // Export each static object as its own .x file
                for (var j = 0; j < envCell.StaticObjects.Count; j++)
                {
                    var stab = envCell.StaticObjects[j];
                    var isSetup = stab.Id >> 24 == 0x2;
                    var isGfxObj = stab.Id >> 24 == 0x1;
                    if (!isSetup && !isGfxObj) continue;

                    var stabFrame = stab.Frame;
                    // Static object transform is relative to cell, so combine: stabRot * cellRot for rotation,
                    // and transform stab position by cell rotation + cell position for world position
                    var stabRotMatrix = Matrix4x4.CreateFromQuaternion(stabFrame.Orientation);
                    var combinedRot = stabRotMatrix * rotMatrix;
                    var worldPos = Vector3.Transform(stabFrame.Origin, rotMatrix) + position;

                    var staticXName = $"static_{i}_{j}_{stab.Id:X8}.x";
                    var staticXPath = Path.Combine(outDir, staticXName);

                    WriteSingleXFile_Object(staticXPath, stab.Id, combinedRot, outDir);

                    artifacts.Add(new DungeonArtifact($"Obj_{i}_{j}", staticXName, worldPos));
                    totalFiles++;
                }
            }

            // Generate VBS creation script
            var vbsFilename = Path.Combine(outDir, $"{worldName}_create.vbs");
            GenerateDungeonScript(worldName, artifacts, vbsFilename);

            MainWindow.Instance.AddStatusText($"Exported {totalFiles} .x files + VBS script to {outDir}");
            return true;
        }

        private class DungeonArtifact
        {
            public string Name;
            public string XFile;
            public Vector3 Position;
            public DungeonArtifact(string name, string xFile, Vector3 position)
            {
                Name = name; XFile = xFile; Position = position;
            }
        }

        /// <summary>
        /// Writes a single .x file containing one CellStruct mesh with rotation baked into vertices.
        /// </summary>
        private static void WriteSingleXFile(string path, string meshName,
            CellStruct cellStruct, List<uint> surfaceIDs,
            Matrix4x4 rotMatrix, string outDir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("xof 0302txt 0064");
            sb.AppendLine();
            sb.AppendLine($"Frame Frame_{meshName} {{");
            sb.AppendLine("  FrameTransformMatrix {");
            sb.AppendLine("    1.0, 0.0, 0.0, 0.0,");
            sb.AppendLine("    0.0, 1.0, 0.0, 0.0,");
            sb.AppendLine("    0.0, 0.0, 1.0, 0.0,");
            sb.AppendLine("    0.0, 0.0, 0.0, 1.0;;");
            sb.AppendLine("  }");
            WriteCellStructMesh_Transformed(sb, cellStruct, surfaceIDs, $"Mesh_{meshName}", rotMatrix, outDir, "  ");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>
        /// Writes a single .x file for a GfxObj or Setup with rotation baked into vertices.
        /// </summary>
        private static void WriteSingleXFile_Object(string path, uint objectId, Matrix4x4 rotMatrix, string outDir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("xof 0302txt 0064");
            sb.AppendLine();
            sb.AppendLine($"Frame Obj_{objectId:X8} {{");
            sb.AppendLine("  FrameTransformMatrix {");
            sb.AppendLine("    1.0, 0.0, 0.0, 0.0,");
            sb.AppendLine("    0.0, 1.0, 0.0, 0.0,");
            sb.AppendLine("    0.0, 0.0, 1.0, 0.0,");
            sb.AppendLine("    0.0, 0.0, 0.0, 1.0;;");
            sb.AppendLine("  }");

            var isSetup = objectId >> 24 == 0x2;
            if (isSetup)
            {
                var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(objectId);
                if (setup == null) return;

                List<ACE.DatLoader.Entity.Frame> placementFrames = null;
                if (setup.PlacementFrames.TryGetValue((int)Placement.Resting, out var placement) ||
                    setup.PlacementFrames.TryGetValue((int)Placement.Default, out placement))
                    placementFrames = placement.AnimFrame.Frames;

                for (var i = 0; i < setup.Parts.Count; i++)
                {
                    var partId = setup.Parts[i];
                    if (partId == 0x010001ec) continue;

                    var partTransform = Matrix4x4.Identity;
                    if (i < setup.DefaultScale.Count && setup.DefaultScale[i] != Vector3.One)
                        partTransform = Matrix4x4.CreateScale(setup.DefaultScale[i]);

                    if (placementFrames != null && i < placementFrames.Count)
                    {
                        var partFrame = placementFrames[i];
                        partTransform *= Matrix4x4.CreateFromQuaternion(partFrame.Orientation) * Matrix4x4.CreateTranslation(partFrame.Origin);
                    }

                    // Bake both part transform and world rotation into vertices
                    var combined = partTransform * rotMatrix;
                    WriteGfxObjMesh_X(sb, partId, combined, outDir, $"Part_{partId:X8}_{i}", "  ");
                }
            }
            else
            {
                WriteGfxObjMesh_X(sb, objectId, rotMatrix, outDir, $"Obj_{objectId:X8}", "  ");
            }

            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>
        /// Writes a CellStruct mesh with rotation baked into vertex positions and normals.
        /// </summary>
        private static void WriteCellStructMesh_Transformed(StringBuilder sb, CellStruct cellStruct,
            List<uint> surfaceIDs, string meshName, Matrix4x4 rotMatrix, string outDir, string indent)
        {
            var vertices = cellStruct.VertexArray.Vertices.OrderBy(i => i.Key).Select(i => i.Value).ToList();
            var polygons = cellStruct.Polygons.OrderBy(i => i.Key).Select(i => i.Value).ToList();

            if (vertices.Count == 0 || polygons.Count == 0) return;

            var xVerts = new List<Vector3>();
            var xNorms = new List<Vector3>();
            var xUVs = new List<Vector2>();
            var xFaces = new List<int[]>();
            var xFaceMats = new List<int>();

            var surfaceIndexSet = new SortedSet<int>();
            foreach (var poly in polygons)
            {
                if (poly.Stippling.HasFlag(StipplingType.NoPos)) continue;
                surfaceIndexSet.Add(poly.PosSurface);
            }
            var surfaceIndexList = surfaceIndexSet.ToList();
            var surfaceRemap = new Dictionary<int, int>();
            for (var i = 0; i < surfaceIndexList.Count; i++)
                surfaceRemap[surfaceIndexList[i]] = i;

            var vertexMap = new Dictionary<long, int>();

            foreach (var poly in polygons)
            {
                if (poly.Stippling.HasFlag(StipplingType.NoPos)) continue;

                var firstIdx = -1;
                var prevIdx = -1;

                for (var i = 0; i < poly.VertexIds.Count; i++)
                {
                    var vid = poly.VertexIds[i];
                    var uvIdx = i < poly.PosUVIndices.Count ? poly.PosUVIndices[i] : (byte)0;
                    long key = ((long)vid << 16) | uvIdx;

                    if (!vertexMap.TryGetValue(key, out var xIdx))
                    {
                        xIdx = xVerts.Count;
                        vertexMap[key] = xIdx;

                        var sv = vertices[vid];
                        // Bake rotation into position and normal
                        xVerts.Add(ACtoVW(Vector3.Transform(sv.Origin, rotMatrix)));
                        xNorms.Add(ACtoVW(Vector3.TransformNormal(sv.Normal, rotMatrix)));

                        if (sv.UVs != null && uvIdx < sv.UVs.Count)
                            xUVs.Add(new Vector2(sv.UVs[uvIdx].U, sv.UVs[uvIdx].V));
                        else
                            xUVs.Add(Vector2.Zero);
                    }

                    if (i == 0) firstIdx = xIdx;
                    else if (i >= 2)
                    {
                        xFaces.Add(new int[] { firstIdx, xIdx, prevIdx });
                        xFaceMats.Add(surfaceRemap.ContainsKey(poly.PosSurface) ? surfaceRemap[poly.PosSurface] : 0);
                    }
                    prevIdx = xIdx;
                }
            }

            if (xVerts.Count == 0 || xFaces.Count == 0) return;

            var materialNames = new List<string>();
            var textureFilenames = new List<string>();

            foreach (var surfIdx in surfaceIndexList)
            {
                if (surfIdx < surfaceIDs.Count)
                {
                    var surfaceID = surfaceIDs[surfIdx];
                    var texName = $"{surfaceID:X8}.gif";
                    materialNames.Add($"Mat_{surfaceID:X8}");
                    textureFilenames.Add(texName);

                    var texPath = Path.Combine(outDir, texName);
                    if (!File.Exists(texPath))
                        ExportImageAsGif(surfaceID, texPath);
                }
                else
                {
                    materialNames.Add($"Mat_Idx{surfIdx}");
                    textureFilenames.Add("");
                }
            }

            WriteXMesh(sb, meshName, xVerts, xNorms, xUVs, xFaces, xFaceMats, materialNames, textureFilenames, indent);
        }

        /// <summary>
        /// Generates a VBS script that creates a VWorlds room and places each artifact.
        /// </summary>
        /// <summary>
        /// Generates a VBS script that places dungeon artifacts into an existing VWorlds room.
        /// Assumes World, Room, and the G() helper are already set up by the caller.
        /// Also generates a standalone version that handles everything.
        /// </summary>
        private static void GenerateDungeonScript(string worldName, List<DungeonArtifact> artifacts, string vbsFilename)
        {
            var contentRelPath = $"worlds\\{worldName}\\";

            // Standalone script that creates the world and places everything
            var sb = new StringBuilder();
            sb.AppendLine("' VWorlds dungeon creation script");
            sb.AppendLine($"' World: {worldName}");
            sb.AppendLine($"' {artifacts.Count} artifact(s)");
            sb.AppendLine("'");
            sb.AppendLine("' Usage: cscript " + worldName + "_create.vbs");
            sb.AppendLine();

            sb.AppendLine("Dim Client, World, Room");
            sb.AppendLine();
            sb.AppendLine("Set Client = CreateObject(\"VWSYSTEM.Client.1\")");
            sb.AppendLine("' Client.Initialize");
            sb.AppendLine($"Set World = Client.ConnectLocal(\"{worldName}\")");
            sb.AppendLine();
            sb.AppendLine("World.CreateCOMModule \"Multimedia\", \"VWSYSTEM.MultimediaEx.1\", 3");
            sb.AppendLine("World.CreateCOMModule \"Studio\", \"VWSTUDIO.StudioEx.1\", 3");
            sb.AppendLine("World.CreateCOMModule \"Foundation\", \"VWEXEMP.FoundationExemplars.1\", 3");
            sb.AppendLine("World.Global.DefaultSpriteFile = \"default.spr\"");
            sb.AppendLine();
            sb.AppendLine($"Set Room = World.CreateInstance(\"{worldName}\", World.Exemplar(\"Room\"))");
            sb.AppendLine("World.Global.DefaultRoom = Room");
            sb.AppendLine("Room.GeometryName = \"\"");
            sb.AppendLine();

            sb.AppendLine("Sub G(name, geom, px, py, pz)");
            sb.AppendLine("    Dim t");
            sb.AppendLine("    Set t = World.CreateInstance(name, World.Exemplar(\"Artifact\"))");
            sb.AppendLine("    If Err.Number = 0 Then");
            sb.AppendLine("        t.MoveInto Room");
            sb.AppendLine("        t.InitializeGraphics geom, px, py, pz, 0.0, 0.0, 1.0");
            sb.AppendLine("        Err.Clear");
            sb.AppendLine("    End If");
            sb.AppendLine("End Sub");
            sb.AppendLine();
            sb.AppendLine($"' --- {artifacts.Count} dungeon pieces ---");

            foreach (var art in artifacts)
            {
                var geomPath = contentRelPath + art.XFile;
                // AC is Z-up, VWorlds is Y-up: swap Y and Z
                sb.AppendLine($"G \"{art.Name}\", \"{geomPath}\", {F(art.Position.X)}, {F(art.Position.Z)}, {F(art.Position.Y)}");
            }

            sb.AppendLine();
            sb.AppendLine("' World.SaveDatabase");

            File.WriteAllText(vbsFilename, sb.ToString());
            MainWindow.Instance.AddStatusText($"Wrote {vbsFilename}");
        }

        /// <summary>
        /// Exports an Environment (0x0D file) with all its CellStructs as a single .X file.
        /// This exports just the geometry templates, not placed instances.
        /// </summary>
        public static bool ExportEnvironment_X(uint environmentID, string outFilename)
        {
            var environment = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.Environment>(environmentID);
            if (environment == null)
            {
                Console.WriteLine($"Failed to load Environment {environmentID:X8}");
                return false;
            }

            var fi = new System.IO.FileInfo(outFilename);
            var outDir = fi.DirectoryName;

            var sb = new StringBuilder();
            sb.AppendLine("xof 0302txt 0064");
            sb.AppendLine();
            sb.AppendLine($"// Exported from Asheron's Call Environment {environmentID:X8}");
            sb.AppendLine($"// Contains {environment.Cells.Count} CellStruct(s)");
            sb.AppendLine();

            // For environments exported standalone, we need to provide surface IDs.
            // Since there's no EnvCell context, we create dummy surface IDs from the polygons.
            // The real surfaces would come from the EnvCell that references this environment.
            // For now, just export geometry with placeholder materials.

            foreach (var kvp in environment.Cells)
            {
                var cellIdx = kvp.Key;
                var cellStruct = kvp.Value;

                sb.AppendLine($"Frame CellStruct_{cellIdx} {{");
                WriteFrameTransformMatrix(sb, Matrix4x4.Identity, "  ");

                // Without an EnvCell, we don't have surface ID mappings.
                // Export with surface indices as material names.
                WriteCellStructMesh_NoSurfaces(sb, cellStruct, $"Struct_{cellIdx}", outDir, "  ");

                sb.AppendLine("}");
                sb.AppendLine();
            }

            File.WriteAllText(outFilename, sb.ToString());
            MainWindow.Instance.AddStatusText($"Wrote {outFilename}");
            return true;
        }

        private static void WriteFrameTransformMatrix(StringBuilder sb, Matrix4x4 m, string indent)
        {
            sb.AppendLine($"{indent}FrameTransformMatrix {{");
            sb.AppendLine($"{indent}  {F(m.M11)}, {F(m.M12)}, {F(m.M13)}, {F(m.M14)},");
            sb.AppendLine($"{indent}  {F(m.M21)}, {F(m.M22)}, {F(m.M23)}, {F(m.M24)},");
            sb.AppendLine($"{indent}  {F(m.M31)}, {F(m.M32)}, {F(m.M33)}, {F(m.M34)},");
            sb.AppendLine($"{indent}  {F(m.M41)}, {F(m.M42)}, {F(m.M43)}, {F(m.M44)};;");
            sb.AppendLine($"{indent}}}");
        }

        private static string F(float v)
        {
            // D3DRM text parser can't handle "-0" - normalize to 0
            if (v == 0f) return "0.0";
            // Clamp very small values that would format as "-0"
            if (Math.Abs(v) < 0.0000005f) return "0.0";
            return v.ToString("0.######");
        }

        /// <summary>
        /// Swizzle AC coordinate system (Z-up) to VWorlds (Y-up): (x, y, z) → (x, z, y)
        /// </summary>
        private static Vector3 ACtoVW(Vector3 v)
        {
            return new Vector3(v.X, v.Z, v.Y);
        }

        private static void WriteCellStructMesh(StringBuilder sb, CellStruct cellStruct, List<uint> surfaceIDs, string meshName, string outDir, string indent)
        {
            var vertices = cellStruct.VertexArray.Vertices.OrderBy(i => i.Key).Select(i => i.Value).ToList();
            var polygons = cellStruct.Polygons.OrderBy(i => i.Key).Select(i => i.Value).ToList();

            if (vertices.Count == 0 || polygons.Count == 0) return;

            // Build denormalized vertex list (one entry per vertex+UV combination)
            // and triangle list from polygon fans
            var xVerts = new List<Vector3>();      // positions
            var xNorms = new List<Vector3>();      // normals
            var xUVs = new List<Vector2>();         // texture coords
            var xFaces = new List<int[]>();         // triangle indices
            var xFaceMats = new List<int>();        // material index per face

            // Collect unique surface indices used
            var surfaceIndexSet = new SortedSet<int>();
            foreach (var poly in polygons)
            {
                if (poly.Stippling.HasFlag(StipplingType.NoPos)) continue;
                surfaceIndexSet.Add(poly.PosSurface);
            }
            var surfaceIndexList = surfaceIndexSet.ToList();
            var surfaceRemap = new Dictionary<int, int>();
            for (var i = 0; i < surfaceIndexList.Count; i++)
                surfaceRemap[surfaceIndexList[i]] = i;

            // Build vertex/UV lookup: for each polygon vertex, emit a unique vertex
            // (AC polygons can reference different UVs for the same vertex position)
            var vertexMap = new Dictionary<long, int>(); // key = (vertexId << 16) | uvIndex

            foreach (var poly in polygons)
            {
                if (poly.Stippling.HasFlag(StipplingType.NoPos)) continue;

                // Convert polygon fan to triangles
                var firstIdx = -1;
                var prevIdx = -1;

                for (var i = 0; i < poly.VertexIds.Count; i++)
                {
                    var vid = poly.VertexIds[i];
                    var uvIdx = i < poly.PosUVIndices.Count ? poly.PosUVIndices[i] : (byte)0;
                    long key = ((long)vid << 16) | uvIdx;

                    if (!vertexMap.TryGetValue(key, out var xIdx))
                    {
                        xIdx = xVerts.Count;
                        vertexMap[key] = xIdx;

                        var sv = vertices[vid];
                        xVerts.Add(ACtoVW(sv.Origin));
                        xNorms.Add(ACtoVW(sv.Normal));

                        if (sv.UVs != null && uvIdx < sv.UVs.Count)
                            xUVs.Add(new Vector2(sv.UVs[uvIdx].U, sv.UVs[uvIdx].V));
                        else
                            xUVs.Add(Vector2.Zero);
                    }

                    if (i == 0)
                        firstIdx = xIdx;
                    else if (i >= 2)
                    {
                        xFaces.Add(new int[] { firstIdx, xIdx, prevIdx });
                        xFaceMats.Add(surfaceRemap.ContainsKey(poly.PosSurface) ? surfaceRemap[poly.PosSurface] : 0);
                    }
                    prevIdx = xIdx;
                }
            }

            if (xVerts.Count == 0 || xFaces.Count == 0) return;

            // Export textures as GIF and build material list
            var materialNames = new List<string>();
            var textureFilenames = new List<string>();

            foreach (var surfIdx in surfaceIndexList)
            {
                if (surfIdx < surfaceIDs.Count)
                {
                    var surfaceID = surfaceIDs[surfIdx];
                    var texName = $"{surfaceID:X8}.gif";
                    materialNames.Add($"Mat_{surfaceID:X8}");
                    textureFilenames.Add(texName);

                    var texPath = Path.Combine(outDir, texName);
                    if (!File.Exists(texPath))
                        ExportImageAsGif(surfaceID, texPath);
                }
                else
                {
                    materialNames.Add($"Mat_Idx{surfIdx}");
                    textureFilenames.Add("");
                }
            }

            WriteXMesh(sb, meshName, xVerts, xNorms, xUVs, xFaces, xFaceMats, materialNames, textureFilenames, indent);
        }

        private static void WriteXMesh(StringBuilder sb, string meshName,
            List<Vector3> xVerts, List<Vector3> xNorms, List<Vector2> xUVs,
            List<int[]> xFaces, List<int> xFaceMats,
            List<string> materialNames, List<string> textureFilenames, string indent)
        {
            if (xVerts.Count == 0 || xFaces.Count == 0) return;

            // Write Mesh
            sb.AppendLine($"{indent}Mesh {meshName} {{");

            // Vertices
            sb.AppendLine($"{indent}  {xVerts.Count};");
            for (var i = 0; i < xVerts.Count; i++)
            {
                var v = xVerts[i];
                var sep = i < xVerts.Count - 1 ? "," : ";";
                sb.AppendLine($"{indent}  {F(v.X)}; {F(v.Y)}; {F(v.Z)};{sep}");
            }

            // Faces
            sb.AppendLine($"{indent}  {xFaces.Count};");
            for (var i = 0; i < xFaces.Count; i++)
            {
                var f = xFaces[i];
                var sep = i < xFaces.Count - 1 ? "," : ";";
                sb.AppendLine($"{indent}  3; {f[0]}, {f[1]}, {f[2]};{sep}");
            }

            // MeshMaterialList
            sb.AppendLine($"{indent}  MeshMaterialList {{");
            sb.AppendLine($"{indent}    {materialNames.Count};");
            sb.AppendLine($"{indent}    {xFaces.Count};");
            for (var i = 0; i < xFaceMats.Count; i++)
            {
                var sep = i < xFaceMats.Count - 1 ? "," : ";";
                sb.Append($"{indent}    {xFaceMats[i]}{sep}");
                if ((i + 1) % 20 == 0 || i == xFaceMats.Count - 1) sb.AppendLine();
            }
            for (var i = 0; i < materialNames.Count; i++)
            {
                sb.AppendLine($"{indent}    Material {materialNames[i]} {{");
                sb.AppendLine($"{indent}      1.0; 1.0; 1.0; 1.0;;");
                sb.AppendLine($"{indent}      10.0;");
                sb.AppendLine($"{indent}      0.2; 0.2; 0.2;;");
                sb.AppendLine($"{indent}      0.0; 0.0; 0.0;;");
                if (!string.IsNullOrEmpty(textureFilenames[i]))
                {
                    sb.AppendLine($"{indent}      TextureFilename {{");
                    sb.AppendLine($"{indent}        \"{textureFilenames[i]}\";");
                    sb.AppendLine($"{indent}      }}");
                }
                sb.AppendLine($"{indent}    }}");
            }
            sb.AppendLine($"{indent}  }}");

            // MeshNormals
            sb.AppendLine($"{indent}  MeshNormals {{");
            sb.AppendLine($"{indent}    {xNorms.Count};");
            for (var i = 0; i < xNorms.Count; i++)
            {
                var n = xNorms[i];
                var sep = i < xNorms.Count - 1 ? "," : ";";
                sb.AppendLine($"{indent}    {F(n.X)}; {F(n.Y)}; {F(n.Z)};{sep}");
            }
            sb.AppendLine($"{indent}    {xFaces.Count};");
            for (var i = 0; i < xFaces.Count; i++)
            {
                var f = xFaces[i];
                var sep = i < xFaces.Count - 1 ? "," : ";";
                sb.AppendLine($"{indent}    3; {f[0]}, {f[1]}, {f[2]};{sep}");
            }
            sb.AppendLine($"{indent}  }}");

            // MeshTextureCoords
            sb.AppendLine($"{indent}  MeshTextureCoords {{");
            sb.AppendLine($"{indent}    {xUVs.Count};");
            for (var i = 0; i < xUVs.Count; i++)
            {
                var uv = xUVs[i];
                var sep = i < xUVs.Count - 1 ? "," : ";";
                sb.AppendLine($"{indent}    {F(uv.X)}; {F(uv.Y)};{sep}");
            }
            sb.AppendLine($"{indent}  }}");

            sb.AppendLine($"{indent}}}"); // end Mesh
        }

        private static void WriteCellStructMesh_NoSurfaces(StringBuilder sb, CellStruct cellStruct, string meshName, string outDir, string indent)
        {
            // Export without real surface IDs - uses surface indices as placeholder materials
            var dummySurfaces = new List<uint>();
            // Find max surface index referenced by polygons
            var maxSurf = 0;
            foreach (var poly in cellStruct.Polygons.Values)
            {
                if (poly.PosSurface > maxSurf) maxSurf = poly.PosSurface;
            }
            for (var i = 0; i <= maxSurf; i++)
                dummySurfaces.Add(0x08000000u | (uint)i);

            WriteCellStructMesh(sb, cellStruct, dummySurfaces, meshName, outDir, indent);
        }

        private static void WriteSetupMesh(StringBuilder sb, uint setupID, string outDir, string indent)
        {
            var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(setupID);
            if (setup == null) return;

            List<ACE.DatLoader.Entity.Frame> placementFrames = null;
            if (setup.PlacementFrames.TryGetValue((int)Placement.Resting, out var placement) ||
                setup.PlacementFrames.TryGetValue((int)Placement.Default, out placement))
                placementFrames = placement.AnimFrame.Frames;

            for (var i = 0; i < setup.Parts.Count; i++)
            {
                var partId = setup.Parts[i];
                if (partId == 0x010001ec) continue; // skip anchor

                var transform = Matrix4x4.Identity;

                if (i < setup.DefaultScale.Count && setup.DefaultScale[i] != Vector3.One)
                    transform = Matrix4x4.CreateScale(setup.DefaultScale[i]);

                if (placementFrames != null && i < placementFrames.Count)
                {
                    var partFrame = placementFrames[i];
                    transform *= Matrix4x4.CreateFromQuaternion(partFrame.Orientation) * Matrix4x4.CreateTranslation(partFrame.Origin);
                }

                sb.AppendLine($"{indent}Frame Part_{i}_{partId:X8} {{");
                WriteFrameTransformMatrix(sb, transform, indent + "  ");
                WriteGfxObjMesh_X(sb, partId, Matrix4x4.Identity, outDir, $"Part_{partId:X8}_{i}", indent + "  ");
                sb.AppendLine($"{indent}}}");
            }
        }

        private static void WriteGfxObjMesh_X(StringBuilder sb, uint gfxObjID, Matrix4x4 transform, string outDir, string meshName, string indent)
        {
            var gfxObj = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.GfxObj>(gfxObjID);
            if (gfxObj == null) return;

            var vertices = gfxObj.VertexArray.Vertices.OrderBy(i => i.Key).Select(i => i.Value).ToList();

            // Build denormalized vertices and triangle faces
            var xVerts = new List<Vector3>();
            var xNorms = new List<Vector3>();
            var xUVs = new List<Vector2>();
            var xFaces = new List<int[]>();
            var xFaceMats = new List<int>();

            var surfaceIndexSet = new SortedSet<int>();
            foreach (var poly in gfxObj.Polygons.OrderBy(i => i.Key).Select(i => i.Value))
            {
                if (poly.Stippling.HasFlag(StipplingType.NoPos)) continue;
                surfaceIndexSet.Add(poly.PosSurface);
            }
            var surfaceIndexList = surfaceIndexSet.ToList();
            var surfaceRemap = new Dictionary<int, int>();
            for (var i = 0; i < surfaceIndexList.Count; i++)
                surfaceRemap[surfaceIndexList[i]] = i;

            var vertexMap = new Dictionary<long, int>();

            foreach (var poly in gfxObj.Polygons.OrderBy(i => i.Key).Select(i => i.Value))
            {
                if (poly.Stippling.HasFlag(StipplingType.NoPos)) continue;

                var firstIdx = -1;
                var prevIdx = -1;

                for (var i = 0; i < poly.VertexIds.Count; i++)
                {
                    var vid = poly.VertexIds[i];
                    var uvIdx = i < poly.PosUVIndices.Count ? poly.PosUVIndices[i] : (byte)0;
                    long key = ((long)vid << 16) | uvIdx;

                    if (!vertexMap.TryGetValue(key, out var xIdx))
                    {
                        xIdx = xVerts.Count;
                        vertexMap[key] = xIdx;

                        var sv = vertices[vid];
                        var pos = Vector3.Transform(sv.Origin, transform);
                        var norm = Vector3.TransformNormal(sv.Normal, transform);

                        xVerts.Add(ACtoVW(pos));
                        xNorms.Add(ACtoVW(norm));

                        if (sv.UVs != null && uvIdx < sv.UVs.Count)
                            xUVs.Add(new Vector2(sv.UVs[uvIdx].U, sv.UVs[uvIdx].V));
                        else
                            xUVs.Add(Vector2.Zero);
                    }

                    if (i == 0)
                        firstIdx = xIdx;
                    else if (i >= 2)
                    {
                        xFaces.Add(new int[] { firstIdx, xIdx, prevIdx });
                        xFaceMats.Add(surfaceRemap.ContainsKey(poly.PosSurface) ? surfaceRemap[poly.PosSurface] : 0);
                    }
                    prevIdx = xIdx;
                }
            }

            if (xVerts.Count == 0 || xFaces.Count == 0) return;

            // Export textures and build materials
            var materialNames = new List<string>();
            var textureFilenames = new List<string>();

            foreach (var surfIdx in surfaceIndexList)
            {
                if (surfIdx < gfxObj.Surfaces.Count)
                {
                    var surfaceID = gfxObj.Surfaces[surfIdx];
                    var texName = $"{surfaceID:X8}.gif";
                    materialNames.Add($"Mat_{surfaceID:X8}");
                    textureFilenames.Add(texName);

                    var texPath = Path.Combine(outDir, texName);
                    if (!File.Exists(texPath))
                        ExportImageAsGif(surfaceID, texPath);
                }
                else
                {
                    materialNames.Add($"Mat_Idx{surfIdx}");
                    textureFilenames.Add("");
                }
            }

            WriteXMesh(sb, meshName, xVerts, xNorms, xUVs, xFaces, xFaceMats, materialNames, textureFilenames, indent);
        }

        /// <summary>
        /// Exports a surface texture as BMP (D3DRM native format).
        /// </summary>
        public static bool ExportImageAsBmp(uint fileID, string outFilename)
        {
            var fileType = fileID >> 24;

            if (fileType == 0x8)
            {
                var surface = DatManager.PortalDat.ReadFromDat<Surface>(fileID);
                fileID = surface.OrigTextureId;
                fileType = 0x05;
            }

            Bitmap highRes = null;

            if (fileType == 0x5)
            {
                var surfaceTexture = DatManager.PortalDat.ReadFromDat<SurfaceTexture>(fileID);
                foreach (var textureID in surfaceTexture.Textures)
                {
                    var bitmap = GetBitmap(textureID);
                    if (bitmap != null && (highRes == null || bitmap.Width * bitmap.Height > highRes.Width * highRes.Height))
                        highRes = bitmap;
                }
            }
            else if (fileType == 0x6)
            {
                highRes = GetBitmap(fileID);
            }

            if (highRes == null) return false;

            highRes.Save(outFilename, System.Drawing.Imaging.ImageFormat.Bmp);
            return true;
        }

        /// <summary>
        /// Exports a surface texture as GIF (VWorlds D3DRM native format).
        /// Falls back to PNG if GIF conversion fails.
        /// </summary>
        public static bool ExportImageAsGif(uint fileID, string outFilename)
        {
            var fileType = fileID >> 24;

            if (fileType == 0x8)
            {
                var surface = DatManager.PortalDat.ReadFromDat<Surface>(fileID);
                fileID = surface.OrigTextureId;
                fileType = 0x05;
            }

            Bitmap highRes = null;

            if (fileType == 0x5)
            {
                var surfaceTexture = DatManager.PortalDat.ReadFromDat<SurfaceTexture>(fileID);
                foreach (var textureID in surfaceTexture.Textures)
                {
                    var bitmap = GetBitmap(textureID);
                    if (bitmap != null && (highRes == null || bitmap.Width * bitmap.Height > highRes.Width * highRes.Height))
                        highRes = bitmap;
                }
            }
            else if (fileType == 0x6)
            {
                highRes = GetBitmap(fileID);
            }

            if (highRes == null) return false;

            try
            {
                highRes.Save(outFilename, System.Drawing.Imaging.ImageFormat.Gif);
            }
            catch
            {
                // GIF has 256 color limit; if conversion fails, save as PNG
                var pngFilename = Path.ChangeExtension(outFilename, ".png");
                highRes.Save(pngFilename);
                Console.WriteLine($"Warning: GIF conversion failed for {fileID:X8}, saved as PNG instead");
            }

            return true;
        }

        /// <summary>
        /// Generates a VBScript that creates a VWorlds room from the exported .X file.
        /// </summary>
        private static void GenerateVWorldsScript(uint envCellID, string xFilename, string vbsFilename)
        {
            var fi = new System.IO.FileInfo(xFilename);
            var xName = fi.Name;
            var worldName = $"AC_{envCellID:X8}";

            var sb = new StringBuilder();
            sb.AppendLine("' VWorlds room creation script");
            sb.AppendLine($"' Generated from Asheron's Call EnvCell {envCellID:X8}");
            sb.AppendLine($"' Place .x and .gif files in: Local Content\\Worlds\\{worldName}\\");
            sb.AppendLine();
            sb.AppendLine("Option Explicit");
            sb.AppendLine();
            sb.AppendLine("Dim Client, World, Room");
            sb.AppendLine();
            sb.AppendLine("Set Client = CreateObject(\"VWSYSTEM.Client.1\")");
            sb.AppendLine("Client.Initialize");
            sb.AppendLine($"Set World = Client.ConnectLocal(\"{worldName}\")");
            sb.AppendLine();
            sb.AppendLine("' Load required modules");
            sb.AppendLine("World.CreateCOMModule \"Multimedia\", \"VWSYSTEM.MultimediaEx.1\", 3");
            sb.AppendLine("World.CreateCOMModule \"Studio\", \"VWSTUDIO.StudioEx.1\", 3");
            sb.AppendLine("World.CreateCOMModule \"Foundation\", \"VWEXEMP.FoundationExemplars.1\", 3");
            sb.AppendLine();
            sb.AppendLine("' Set defaults");
            sb.AppendLine("World.Global.DefaultSpriteFile = \"default.spr\"");
            sb.AppendLine("World.Global.DefaultAvatarExemplar.InitializeSpriteGraphics \"default.spr\", 0.0, 1.0, 0.0, 1.0, 0.0, 0.0");
            sb.AppendLine();
            sb.AppendLine($"' Create the room");
            sb.AppendLine($"Set Room = World.CreateInstance(\"{worldName}\", World.Exemplar(\"Room\"))");
            sb.AppendLine($"World.Global.DefaultRoom = Room");
            sb.AppendLine($"Room.GeometryName = \"\"");
            sb.AppendLine();
            sb.AppendLine($"' Add the AC room geometry as an artifact");
            sb.AppendLine($"Dim RoomGeom");
            sb.AppendLine($"Set RoomGeom = World.CreateInstance(\"ACRoom\", World.Exemplar(\"Artifact\"))");
            sb.AppendLine($"RoomGeom.MoveInto Room");
            sb.AppendLine($"RoomGeom.InitializeGraphics \"worlds\\{worldName}\\{xName}\", 0.0, 0.0, 0.0, 0.0, 0.0, 1.0");
            sb.AppendLine();
            sb.AppendLine("' Save the world");
            sb.AppendLine("World.SaveDatabase");
            sb.AppendLine();
            sb.AppendLine("WScript.Echo \"World created: \" & World.Name");
            sb.AppendLine("Client.Disconnect");

            File.WriteAllText(vbsFilename, sb.ToString());
            MainWindow.Instance.AddStatusText($"Wrote {vbsFilename}");
        }
    }
}
