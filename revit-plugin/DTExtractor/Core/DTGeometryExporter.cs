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

        private string _currentGuid;
        private string _currentName;
        private MaterialNode _currentMaterial;
        private readonly Stack<Transform> _transformStack = new Stack<Transform>();
        private bool _currentElementHasGeometry;

        private int _elementCount;
        private int _polymeshCount;
        private int _polymeshFailCount;
        private readonly string _logPath;

        public int ElementCount => _elementCount;
        public int PolymeshCount => _polymeshCount;
        public int PolymeshFailCount => _polymeshFailCount;

        public DTGeometryExporter(Document doc, string outputPath)
        {
            _doc = doc;
            _gltfBuilder = new DTGltfBuilder(outputPath);
            _metadataCollector = new DTMetadataCollector();
            _meshHashMap = new Dictionary<string, int>();
            _logPath = System.IO.Path.ChangeExtension(outputPath, ".export-log.txt");
        }

        public bool Start()
        {
            try
            {
                System.IO.File.WriteAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] Export started.\r\n");
            }
            catch { }
            return true;
        }

        public void Finish()
        {
            try
            {
                System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] Export callback finished. elements={_elementCount}, polymeshes={_polymeshCount}, polymesh_failures={_polymeshFailCount}\r\n");
            }
            catch { }
        }

        public void Serialize()
        {
            try { System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] Writing GLB and Parquet...\r\n"); } catch { }

            _gltfBuilder.SerializeToGlb();
            try { System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] GLB written.\r\n"); } catch { }

            _metadataCollector.SerializeToParquet(_gltfBuilder.OutputPath);

            var parquetGuids = _metadataCollector.GetAllGuids();
            var gltfGuids = _gltfBuilder.GetAllGuids();

            try
            {
                System.IO.File.AppendAllText(_logPath,
                    $"[{DateTime.Now:HH:mm:ss}] Parquet written. records={parquetGuids.Count}, geometry_instances={gltfGuids.Count}, polymesh_failures={_polymeshFailCount}/{_polymeshCount}\r\n");

                if (gltfGuids.Count > 0 && !gltfGuids.SetEquals(parquetGuids))
                {
                    var onlyInGlb = new HashSet<string>(gltfGuids);
                    onlyInGlb.ExceptWith(parquetGuids);
                    var onlyInParquet = new HashSet<string>(parquetGuids);
                    onlyInParquet.ExceptWith(gltfGuids);

                    System.IO.File.AppendAllText(_logPath,
                        $"[{DateTime.Now:HH:mm:ss}] GUID coverage: only_in_GLB={onlyInGlb.Count}, only_in_Parquet={onlyInParquet.Count}\r\n");
                }
            }
            catch { }
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
            var element = _doc.GetElement(elementId);
            if (element == null || !element.IsValidObject)
                return RenderNodeAction.Skip;

            _currentGuid = element.UniqueId;
            _currentName = element.Name;
            _currentElementHasGeometry = false;
            _elementCount++;

            try
            {
                _metadataCollector.ExtractElement(element);
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] Element #{_elementCount} ExtractElement failed: {ex.Message}\r\n");
                }
                catch { }
            }

            if (_elementCount % 5000 == 0)
            {
                try
                {
                    System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] Progress: elements={_elementCount}, polymeshes={_polymeshCount}\r\n");
                }
                catch { }
            }

            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            _currentGuid = null;
            _currentName = null;
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            try
            {
                var parent = _transformStack.Count > 0 ? _transformStack.Peek() : Transform.Identity;
                _transformStack.Push(parent.Multiply(node.GetTransform()));
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] OnInstanceBegin failed: {ex.Message}\r\n"); } catch { }
                return RenderNodeAction.Skip;
            }
            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node)
        {
            if (_transformStack.Count > 0)
                _transformStack.Pop();
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
            if (string.IsNullOrEmpty(_currentGuid))
                return;

            _polymeshCount++;

            try
            {
                var points = polymesh.GetPoints();
                var facets = polymesh.GetFacets();

                if (points.Count == 0 || facets.Count == 0)
                    return;

                IList<XYZ> rawNormals = null;
                DistributionOfNormals distribution = DistributionOfNormals.OnEachFacet;
                try
                {
                    distribution = polymesh.DistributionOfNormals;
                    rawNormals = polymesh.GetNormals();
                }
                catch { }

                IList<UV> rawUvs = null;
                try { rawUvs = polymesh.GetUVs(); }
                catch { }

                List<XYZ> vertices;
                List<XYZ> normalsList;
                List<UV> uvList;
                List<int> indices;

                if (rawNormals != null && distribution == DistributionOfNormals.AtEachPoint)
                {
                    vertices = points.ToList();
                    normalsList = rawNormals.ToList();
                    uvList = rawUvs != null && rawUvs.Count > 0 ? rawUvs.Cast<UV>().ToList() : null;
                    indices = new List<int>(facets.Count * 3);
                    for (int i = 0; i < facets.Count; i++)
                    {
                        indices.Add(facets[i].V1);
                        indices.Add(facets[i].V2);
                        indices.Add(facets[i].V3);
                    }
                }
                else
                {
                    vertices = new List<XYZ>(facets.Count * 3);
                    normalsList = new List<XYZ>(facets.Count * 3);
                    uvList = rawUvs != null && rawUvs.Count > 0 ? new List<UV>(facets.Count * 3) : null;
                    indices = new List<int>(facets.Count * 3);

                    for (int fi = 0; fi < facets.Count; fi++)
                    {
                        var facet = facets[fi];
                        int baseIdx = vertices.Count;

                        var p0 = points[facet.V1];
                        var p1 = points[facet.V2];
                        var p2 = points[facet.V3];
                        vertices.Add(p0);
                        vertices.Add(p1);
                        vertices.Add(p2);

                        if (rawNormals == null)
                        {
                            var edge1 = p1 - p0;
                            var edge2 = p2 - p0;
                            var cross = edge1.CrossProduct(edge2);
                            var normal = cross.GetLength() > 1e-10 ? cross.Normalize() : XYZ.BasisZ;
                            normalsList.Add(normal);
                            normalsList.Add(normal);
                            normalsList.Add(normal);
                        }
                        else if (distribution == DistributionOfNormals.OnePerFace)
                        {
                            var n = rawNormals[0];
                            normalsList.Add(n);
                            normalsList.Add(n);
                            normalsList.Add(n);
                        }
                        else
                        {
                            normalsList.Add(rawNormals[fi * 3]);
                            normalsList.Add(rawNormals[fi * 3 + 1]);
                            normalsList.Add(rawNormals[fi * 3 + 2]);
                        }

                        if (uvList != null)
                        {
                            uvList.Add(rawUvs.Count > facet.V1 ? rawUvs[facet.V1] : new UV(0, 0));
                            uvList.Add(rawUvs.Count > facet.V2 ? rawUvs[facet.V2] : new UV(0, 0));
                            uvList.Add(rawUvs.Count > facet.V3 ? rawUvs[facet.V3] : new UV(0, 0));
                        }

                        indices.Add(baseIdx);
                        indices.Add(baseIdx + 1);
                        indices.Add(baseIdx + 2);
                    }
                }

                string meshHash = ComputeMeshHash(points, facets);
                var currentTransform = _transformStack.Count > 0 ? _transformStack.Peek() : Transform.Identity;

                if (_meshHashMap.ContainsKey(meshHash))
                {
                    _gltfBuilder.AddInstance(_meshHashMap[meshHash], currentTransform, _currentGuid, _currentName);
                }
                else
                {
                    var materialData = ExtractMaterialData(_currentMaterial);
                    int meshIndex = _gltfBuilder.AddMesh(vertices, normalsList, uvList, indices, materialData);
                    _meshHashMap[meshHash] = meshIndex;
                    _gltfBuilder.AddInstance(meshIndex, currentTransform, _currentGuid, _currentName);
                }

                _currentElementHasGeometry = true;
            }
            catch (Exception ex)
            {
                _polymeshFailCount++;
                try
                {
                    var stLines = ex.StackTrace?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var firstLine = stLines != null && stLines.Length > 0 ? stLines[0].Trim() : "N/A";
                    System.IO.File.AppendAllText(_logPath,
                        $"[{DateTime.Now:HH:mm:ss}] OnPolymesh failed for {_currentGuid}: [{ex.GetType().FullName}] {ex.Message}\r\n" +
                        $"  at: {firstLine}\r\n");
                }
                catch { }
            }
        }

        public void OnRPC(RPCNode node)
        {
            // RPC nodes not supported
        }

        public RenderNodeAction OnFaceBegin(FaceNode node)
        {
            return RenderNodeAction.Proceed;
        }

        public void OnFaceEnd(FaceNode node)
        {
        }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
        }

        private string ComputeMeshHash(IList<XYZ> points, IList<PolymeshFacet> facets)
        {
            using (var sha256 = SHA256.Create())
            {
                var sb = new StringBuilder();
                for (int i = 0; i < Math.Min(10, points.Count); i++)
                {
                    sb.Append($"{points[i].X:F3},{points[i].Y:F3},{points[i].Z:F3};");
                }
                sb.Append($"|{points.Count}|{facets.Count}");
                for (int i = 0; i < Math.Min(10, facets.Count); i++)
                {
                    sb.Append($"|{facets[i].V1},{facets[i].V2},{facets[i].V3}");
                }

                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return Convert.ToBase64String(hashBytes);
            }
        }

        private MaterialData ExtractMaterialData(MaterialNode materialNode)
        {
            if (materialNode == null)
                return MaterialData.Default;

            try
            {
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
            catch
            {
                return MaterialData.Default;
            }
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
