using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using DTExtractor.Models;

namespace DTExtractor.Core
{
    /// <summary>
    /// IExportContext implementation for geometry extraction via CustomExporter
    /// Exports tessellated mesh data with instancing optimization
    /// </summary>
    public class DTGeometryExporter : IExportContext
    {
        private readonly Document _doc;
        private readonly DTGltfBuilder _gltfBuilder;
        private readonly DTMetadataCollector _metadataCollector;
        private readonly Dictionary<string, int> _meshHashMap;

        private Element _currentElement;
        private string _currentGuid;
        private MaterialNode _currentMaterial;
        private Transform _currentTransform = Transform.Identity;

        public DTGeometryExporter(Document doc, string outputPath)
        {
            _doc = doc;
            _gltfBuilder = new DTGltfBuilder(outputPath);
            _metadataCollector = new DTMetadataCollector();
            _meshHashMap = new Dictionary<string, int>();
        }

        public bool Start()
        {
            return true;
        }

        public void Finish()
        {
            // Serialize both outputs
            _gltfBuilder.SerializeToGlb();
            _metadataCollector.SerializeToParquet(_gltfBuilder.OutputPath);

            // GUID consistency validation
            var gltfGuids = _gltfBuilder.GetAllGuids();
            var parquetGuids = _metadataCollector.GetAllGuids();

            if (!gltfGuids.SetEquals(parquetGuids))
            {
                throw new InvalidOperationException(
                    "GUID mismatch between GLB and Parquet outputs. " +
                    $"GLB: {gltfGuids.Count}, Parquet: {parquetGuids.Count}");
            }
        }

        public bool IsCanceled()
        {
            return false;
        }

        public RenderNodeAction OnViewBegin(ViewNode node)
        {
            // Set tessellation quality (LOD control)
            node.LevelOfDetail = 8; // 0-15, higher = more detail
            return RenderNodeAction.Proceed;
        }

        public void OnViewEnd(ElementId elementId)
        {
            // View completed
        }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            _currentElement = _doc.GetElement(elementId);
            if (_currentElement == null)
                return RenderNodeAction.Skip;

            _currentGuid = _currentElement.UniqueId;

            // Extract metadata simultaneously with geometry
            var record = _metadataCollector.ExtractElement(_currentElement);

            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            _currentElement = null;
            _currentGuid = null;
            _currentTransform = Transform.Identity;
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            _currentTransform = node.GetTransform();
            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node)
        {
            _currentTransform = Transform.Identity;
        }

        public void OnLight(LightNode node)
        {
            // Lights are handled separately
        }

        public void OnMaterial(MaterialNode node)
        {
            _currentMaterial = node;
        }

        public void OnPolymesh(PolymeshTopology polymesh)
        {
            if (_currentElement == null || string.IsNullOrEmpty(_currentGuid))
                return;

            // Extract vertex data
            var points = polymesh.GetPoints();
            var normals = polymesh.GetNormals();
            var uvs = polymesh.GetUVs();
            var facets = polymesh.GetFacets();

            if (points.Count == 0 || facets.NumberOfFacets == 0)
                return;

            // Transform to world space
            var vertices = new List<XYZ>();
            var normalsList = new List<XYZ>();

            foreach (var point in points)
            {
                vertices.Add(_currentTransform.OfPoint(point));
            }

            foreach (var normal in normals)
            {
                normalsList.Add(_currentTransform.OfVector(normal).Normalize());
            }

            // Build triangle indices
            var indices = new List<int>();
            for (int i = 0; i < facets.NumberOfFacets; i++)
            {
                var facet = facets.get_Facet(i);
                // Revit facets are always triangulated
                indices.Add(facet.V1);
                indices.Add(facet.V2);
                indices.Add(facet.V3);
            }

            // Compute mesh hash for instancing detection
            string meshHash = ComputeMeshHash(vertices, indices);

            if (_meshHashMap.ContainsKey(meshHash))
            {
                // Reuse existing mesh - add instance
                int meshIndex = _meshHashMap[meshHash];
                _gltfBuilder.AddInstance(meshIndex, _currentTransform, _currentGuid, _currentElement.Name);
            }
            else
            {
                // Create new mesh
                var materialData = ExtractMaterialData(_currentMaterial);
                int meshIndex = _gltfBuilder.AddMesh(
                    vertices,
                    normalsList,
                    uvs.Count > 0 ? uvs.Cast<UV>().ToList() : null,
                    indices,
                    materialData
                );

                _meshHashMap[meshHash] = meshIndex;
                _gltfBuilder.AddInstance(meshIndex, _currentTransform, _currentGuid, _currentElement.Name);
            }
        }

        public void OnRPC(RPCNode node)
        {
            // RPC nodes not supported
        }

        public void OnFaceBegin(FaceNode node)
        {
            // Face-level callbacks not needed
        }

        public void OnFaceEnd(FaceNode node)
        {
        }

        public void OnLinkBegin(LinkNode node)
        {
            // Linked models handled separately
        }

        public void OnLinkEnd(LinkNode node)
        {
        }

        private string ComputeMeshHash(List<XYZ> vertices, List<int> indices)
        {
            using (var sha256 = SHA256.Create())
            {
                var sb = new StringBuilder();
                // Sample first 10 vertices and all indices for hash
                for (int i = 0; i < Math.Min(10, vertices.Count); i++)
                {
                    sb.Append($"{vertices[i].X:F3},{vertices[i].Y:F3},{vertices[i].Z:F3};");
                }
                sb.Append($"|{indices.Count}");

                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return Convert.ToBase64String(hashBytes);
            }
        }

        private MaterialData ExtractMaterialData(MaterialNode materialNode)
        {
            if (materialNode == null)
                return MaterialData.Default;

            return new MaterialData
            {
                Color = new double[]
                {
                    materialNode.Color.Red / 255.0,
                    materialNode.Color.Green / 255.0,
                    materialNode.Color.Blue / 255.0
                },
                Transparency = materialNode.Transparency,
                Smoothness = materialNode.Smoothness
            };
        }

        public List<DTElementRecord> GetExtractedRecords()
        {
            return _metadataCollector.GetAllRecords();
        }
    }

    public class MaterialData
    {
        public double[] Color { get; set; }
        public double Transparency { get; set; }
        public double Smoothness { get; set; }

        public static MaterialData Default => new MaterialData
        {
            Color = new double[] { 0.8, 0.8, 0.8 },
            Transparency = 0.0,
            Smoothness = 0.5
        };
    }
}
