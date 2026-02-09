using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Nodes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;

namespace DTExtractor.Core
{
    public class DTGltfBuilder
    {
        public string OutputPath { get; }

        private readonly ModelRoot _model;
        private readonly Scene _scene;
        private readonly Dictionary<string, Material> _materialCache;
        private readonly HashSet<string> _guidSet;

        public DTGltfBuilder(string outputPath)
        {
            OutputPath = outputPath;
            _model = ModelRoot.CreateModel();
            _scene = _model.UseScene("default");
            _materialCache = new Dictionary<string, Material>();
            _guidSet = new HashSet<string>();
        }

        public void AddElement(string guid, string name, Dictionary<string, ElementGeometryBuffer> geometry)
        {
            if (string.IsNullOrEmpty(guid) || geometry == null || geometry.Count == 0)
                return;

            var mesh = _model.CreateMesh(guid);

            foreach (var kvp in geometry)
            {
                var buf = kvp.Value;
                if (buf.VertexCount == 0 || buf.Indices.Count == 0)
                    continue;

                var positions = buf.GetPositionArray();
                var normals = buf.GetNormalArray();
                var indices = buf.Indices.ToArray();
                var material = GetOrCreateMaterial(buf.MaterialData);

                var prim = mesh.CreatePrimitive()
                    .WithVertexAccessor("POSITION", positions)
                    .WithVertexAccessor("NORMAL", normals)
                    .WithIndicesAccessor(PrimitiveType.TRIANGLES, indices)
                    .WithMaterial(material);

                var uvs = buf.GetUVArray();
                if (uvs != null && uvs.Length == positions.Length)
                    prim.WithVertexAccessor("TEXCOORD_0", uvs);
            }

            if (mesh.Primitives.Count == 0)
                return;

            var node = _scene.CreateNode(guid);
            node.WithMesh(mesh);

            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { guid, name = name ?? "" });
                node.Extras = JsonNode.Parse(json);
            }
            catch { }

            _guidSet.Add(guid);
        }

        private Material GetOrCreateMaterial(MaterialData data)
        {
            var key = data.GetKey();
            if (_materialCache.TryGetValue(key, out var cached))
                return cached;

            float lr = SrgbToLinear(data.Color[0]);
            float lg = SrgbToLinear(data.Color[1]);
            float lb = SrgbToLinear(data.Color[2]);
            float alpha = 1f - data.Transparency;

            var builder = new MaterialBuilder($"mat_{_materialCache.Count}")
                .WithMetallicRoughnessShader()
                .WithBaseColor(new Vector4(lr, lg, lb, alpha))
                .WithMetallicRoughness(
                    metallic: data.Metallic,
                    roughness: data.Roughness);

            if (alpha < 0.999f)
                builder.WithAlpha(SharpGLTF.Materials.AlphaMode.BLEND);

            var material = _model.CreateMaterial(builder);
            material.DoubleSided = true;
            _materialCache[key] = material;
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
                    version = "2.0.0",
                    guidCount = _guidSet.Count,
                    extractedAt = DateTime.UtcNow.ToString("o")
                });
                _model.Extras = JsonNode.Parse(json);
            }
            catch { }

            var settings = new WriteSettings { JsonIndented = false };
            _model.SaveGLB(glbPath, settings);
        }

        public HashSet<string> GetAllGuids()
        {
            return new HashSet<string>(_guidSet);
        }

        private static float SrgbToLinear(float srgb)
        {
            return srgb <= 0.04045f
                ? srgb / 12.92f
                : (float)Math.Pow((srgb + 0.055f) / 1.055f, 2.4);
        }
    }
}
