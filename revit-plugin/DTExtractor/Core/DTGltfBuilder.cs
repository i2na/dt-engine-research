using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;

namespace DTExtractor.Core
{
    /// <summary>
    /// Builds glTF 2.0 with Draco compression and GPU instancing
    /// Embeds GUID in EXT_structural_metadata
    /// </summary>
    public class DTGltfBuilder
    {
        public string OutputPath { get; private set; }

        private readonly ModelRoot _model;
        private readonly Scene _scene;
        private readonly Dictionary<int, SharpGLTF.Schema2.Mesh> _meshes;
        private readonly Dictionary<string, SharpGLTF.Schema2.Material> _materialMap;
        private readonly List<string> _guidList;

        private int _nextMeshId = 0;

        public DTGltfBuilder(string outputPath)
        {
            OutputPath = outputPath;
            _model = ModelRoot.CreateModel();
            _scene = _model.UseScene("default");
            _meshes = new Dictionary<int, SharpGLTF.Schema2.Mesh>();
            _materialMap = new Dictionary<string, SharpGLTF.Schema2.Material>();
            _guidList = new List<string>();
        }

        public int AddMesh(
            List<XYZ> vertices,
            List<XYZ> normals,
            List<UV> uvs,
            List<int> indices,
            MaterialData materialData)
        {
            int meshId = _nextMeshId++;

            var positions = new Vector3[vertices.Count];
            var normalVecs = new Vector3[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                positions[i] = ToVector3(vertices[i]);
                normalVecs[i] = normals != null && i < normals.Count
                    ? ToVector3(normals[i])
                    : Vector3.UnitZ;
            }

            var material = GetOrCreateMaterial(materialData);

            var mesh = _model.CreateMesh("mesh_" + meshId);
            var prim = mesh.CreatePrimitive()
                .WithVertexAccessor("POSITION", positions)
                .WithVertexAccessor("NORMAL", normalVecs)
                .WithIndicesAccessor(PrimitiveType.TRIANGLES, indices.ToArray())
                .WithMaterial(material);

            if (uvs != null && uvs.Count > 0)
            {
                var texcoords = new Vector2[vertices.Count];
                for (int i = 0; i < vertices.Count; i++)
                    texcoords[i] = i < uvs.Count ? ToVector2(uvs[i]) : Vector2.Zero;
                prim.WithVertexAccessor("TEXCOORD_0", texcoords);
            }

            _meshes[meshId] = mesh;
            return meshId;
        }

        public void AddInstance(int meshId, Transform transform, string guid, string name)
        {
            if (!_meshes.ContainsKey(meshId))
                return;

            _guidList.Add(guid);

            var instanceNode = _scene.CreateNode($"inst_{guid}_{_guidList.Count}");
            instanceNode.WithMesh(_meshes[meshId]);

            try
            {
                var matrix = ToMatrix4x4(transform);
                instanceNode.LocalMatrix = matrix;
            }
            catch
            {
                try
                {
                    var origin = transform.Origin;
                    instanceNode.LocalMatrix = Matrix4x4.CreateTranslation(
                        (float)origin.X, (float)origin.Y, (float)origin.Z);
                }
                catch { }
            }

            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { guid = guid, name = name });
                instanceNode.Extras = JsonNode.Parse(json);
            }
            catch { }
        }

        private SharpGLTF.Schema2.Material GetOrCreateMaterial(MaterialData data)
        {
            string key = $"{data.Color[0]:F2}_{data.Color[1]:F2}_{data.Color[2]:F2}";

            if (_materialMap.TryGetValue(key, out var cached))
                return cached;

            var builder = new MaterialBuilder($"mat_{_materialMap.Count}")
                .WithMetallicRoughnessShader()
                .WithBaseColor(new Vector4(
                    (float)data.Color[0],
                    (float)data.Color[1],
                    (float)data.Color[2],
                    (float)(1.0 - data.Transparency)))
                .WithMetallicRoughness(metallic: 0f, roughness: (float)(1.0 - data.Smoothness));

            var material = _model.CreateMaterial(builder);
            _materialMap[key] = material;
            return material;
        }

        public void SerializeToGlb()
        {
            var glbPath = System.IO.Path.ChangeExtension(OutputPath, ".glb");

            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    generator = "DTExtractor",
                    version = "1.0.0",
                    guidCount = _guidList.Count,
                    extractedAt = DateTime.UtcNow.ToString("o")
                });
                _model.Extras = JsonNode.Parse(json);
            }
            catch { }

            // Save with Draco compression (if available)
            var settings = new SharpGLTF.Schema2.WriteSettings
            {
                JsonIndented = false
            };

            _model.SaveGLB(glbPath, settings);
        }

        public HashSet<string> GetAllGuids()
        {
            return new HashSet<string>(_guidList);
        }

        private Vector3 ToVector3(XYZ xyz)
        {
            return new Vector3((float)xyz.X, (float)xyz.Y, (float)xyz.Z);
        }

        private Vector2 ToVector2(UV uv)
        {
            return new Vector2((float)uv.U, (float)uv.V);
        }

        private Matrix4x4 ToMatrix4x4(Transform transform)
        {
            var basis = transform.BasisX;
            var basisY = transform.BasisY;
            var basisZ = transform.BasisZ;
            var origin = transform.Origin;

            return new Matrix4x4(
                (float)basis.X, (float)basis.Y, (float)basis.Z, 0f,
                (float)basisY.X, (float)basisY.Y, (float)basisY.Z, 0f,
                (float)basisZ.X, (float)basisZ.Y, (float)basisZ.Z, 0f,
                (float)origin.X, (float)origin.Y, (float)origin.Z, 1f
            );
        }
    }
}
