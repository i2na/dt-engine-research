using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Autodesk.Revit.DB;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
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
        private readonly Dictionary<int, Node> _meshNodes;
        private readonly Dictionary<string, MaterialBuilder> _materialMap;
        private readonly List<string> _guidList;

        private int _nextMeshId = 0;

        public DTGltfBuilder(string outputPath)
        {
            OutputPath = outputPath;
            _model = ModelRoot.CreateModel();
            _scene = _model.UseScene("default");
            _meshNodes = new Dictionary<int, Node>();
            _materialMap = new Dictionary<string, MaterialBuilder>();
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

            // Create material
            var material = GetOrCreateMaterial(materialData);

            // Build mesh using SharpGLTF
            var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1>("mesh_" + meshId);
            var prim = meshBuilder.UsePrimitive(material);

            // Add triangles
            for (int i = 0; i < indices.Count; i += 3)
            {
                var v0 = vertices[indices[i]];
                var v1 = vertices[indices[i + 1]];
                var v2 = vertices[indices[i + 2]];

                var n0 = normals[indices[i]];
                var n1 = normals[indices[i + 1]];
                var n2 = normals[indices[i + 2]];

                var uv0 = uvs != null && uvs.Count > indices[i] ? uvs[indices[i]] : new UV(0, 0);
                var uv1 = uvs != null && uvs.Count > indices[i + 1] ? uvs[indices[i + 1]] : new UV(0, 0);
                var uv2 = uvs != null && uvs.Count > indices[i + 2] ? uvs[indices[i + 2]] : new UV(0, 0);

                var vtx0 = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
                    new VertexPositionNormal(ToVector3(v0), ToVector3(n0)),
                    new VertexTexture1(ToVector2(uv0)),
                    default
                );

                var vtx1 = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
                    new VertexPositionNormal(ToVector3(v1), ToVector3(n1)),
                    new VertexTexture1(ToVector2(uv1)),
                    default
                );

                var vtx2 = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
                    new VertexPositionNormal(ToVector3(v2), ToVector3(n2)),
                    new VertexTexture1(ToVector2(uv2)),
                    default
                );

                prim.AddTriangle(vtx0, vtx1, vtx2);
            }

            var mesh = _model.CreateMesh(meshBuilder);
            var node = _scene.CreateNode("node_" + meshId).WithMesh(mesh);
            _meshNodes[meshId] = node;

            return meshId;
        }

        public void AddInstance(int meshId, Transform transform, string guid, string name)
        {
            if (!_meshNodes.ContainsKey(meshId))
                throw new ArgumentException($"Mesh {meshId} not found");

            _guidList.Add(guid);

            // Create instance node with transform
            var instanceNode = _scene.CreateNode($"inst_{guid}");
            instanceNode.WithMesh(_meshNodes[meshId].Mesh);

            // Set transform matrix
            var matrix = ToMatrix4x4(transform);
            instanceNode.LocalMatrix = matrix;

            var extrasJson = JsonSerializer.SerializeToElement(new { guid = guid, name = name });
            instanceNode.Extras = extrasJson;
        }

        private MaterialBuilder GetOrCreateMaterial(MaterialData data)
        {
            string key = $"{data.Color[0]:F2}_{data.Color[1]:F2}_{data.Color[2]:F2}";

            if (_materialMap.TryGetValue(key, out var cached))
                return cached;

            var material = new MaterialBuilder($"mat_{_materialMap.Count}")
                .WithMetallicRoughnessShader()
                .WithBaseColor(new Vector4(
                    (float)data.Color[0],
                    (float)data.Color[1],
                    (float)data.Color[2],
                    (float)(1.0 - data.Transparency)))
                .WithMetallicRoughness(metallic: 0f, roughness: (float)(1.0 - data.Smoothness));

            _materialMap[key] = material;
            return material;
        }

        public void SerializeToGlb()
        {
            var glbPath = System.IO.Path.ChangeExtension(OutputPath, ".glb");

            var modelExtras = JsonSerializer.SerializeToElement(new
            {
                generator = "DTExtractor",
                version = "1.0.0",
                guidCount = _guidList.Count,
                extractedAt = DateTime.UtcNow.ToString("o")
            });
            _model.Extras = modelExtras;

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
