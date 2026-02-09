using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using DTExtractor.Models;

namespace DTExtractor.Core
{
    public class DTGeometryExporter : IExportContext
    {
        private readonly Document _hostDoc;
        private Document _currentDoc;
        private readonly DTGltfBuilder _gltfBuilder;
        private readonly DTMetadataCollector _metadataCollector;
        private readonly string _outputPath;

        private readonly Stack<Transform> _transformStack = new Stack<Transform>();
        private Transform CurrentTransform => _transformStack.Count > 0 ? _transformStack.Peek() : Transform.Identity;

        private Element _currentElement;
        private string _currentGuid;
        private string _currentName;
        private MaterialData _currentMaterialData;
        private string _currentMaterialKey;
        private Dictionary<string, ElementGeometryBuffer> _elementGeometry;
        private Transform _linkTransform;
        private bool _isLink;

        private int _elementCount;
        private int _polymeshCount;
        private int _polymeshFailCount;
        private int _estimatedTotalElements;

        private readonly StreamWriter _logWriter;
        private readonly string _logPath;
        private bool _logClosed;
        private IDTProgressIndicator _progressIndicator;

        public int ElementCount => _elementCount;
        public int PolymeshCount => _polymeshCount;
        public int PolymeshFailCount => _polymeshFailCount;

        private static readonly Dictionary<string, string> ColorPropertyMap = new Dictionary<string, string>
        {
            { "PrismGenericSchema", "generic_diffuse" },
            { "GenericSchema", "generic_diffuse" },
            { "ConcreteSchema", "concrete_color" },
            { "WallPaintSchema", "wallpaint_color" },
            { "PlasticVinylSchema", "plasticvinyl_color" },
            { "MetallicPaintSchema", "metallicpaint_base_color" },
            { "CeramicSchema", "ceramic_color" },
            { "MetalSchema", "metal_color" },
            { "PrismMetalSchema", "adsklib_Metal_F0" },
            { "PrismOpaqueSchema", "opaque_albedo" },
            { "PrismLayeredSchema", "adsklib_Layered_Diffuse" },
            { "HardwoodSchema", "hardwood_color" },
            { "PrismMasonryCMUSchema", "masonrycmu_color" },
            { "MasonryCMUSchema", "masonrycmu_color" },
        };

        public DTGeometryExporter(Document doc, string outputPath)
        {
            _hostDoc = doc;
            _currentDoc = doc;
            _outputPath = outputPath;
            _gltfBuilder = new DTGltfBuilder(outputPath);
            _metadataCollector = new DTMetadataCollector();
            _logPath = Path.ChangeExtension(outputPath, ".export-log.txt");
            _logWriter = new StreamWriter(_logPath, false, Encoding.UTF8) { AutoFlush = false };

            try
            {
                _estimatedTotalElements = new FilteredElementCollector(_hostDoc)
                    .WhereElementIsNotElementType()
                    .GetElementCount();
            }
            catch { _estimatedTotalElements = 10000; }
        }

        public void SetProgressIndicator(IDTProgressIndicator indicator) => _progressIndicator = indicator;

        #region IExportContext

        public bool Start()
        {
            _transformStack.Clear();
            _transformStack.Push(Transform.Identity);
            _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] Export started. Estimated elements: {_estimatedTotalElements}");
            _logWriter.Flush();
            _progressIndicator?.Report(0, _estimatedTotalElements);
            return true;
        }

        public void Finish()
        {
            _progressIndicator?.Report(_elementCount, _estimatedTotalElements);
            _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] Export finished. elements={_elementCount}, polymeshes={_polymeshCount}, failures={_polymeshFailCount}");
            _logWriter.Flush();
        }

        public bool IsCanceled() => false;

        public RenderNodeAction OnViewBegin(ViewNode node)
        {
            node.LevelOfDetail = 8;
            return RenderNodeAction.Proceed;
        }

        public void OnViewEnd(ElementId elementId) { }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            var element = _currentDoc.GetElement(elementId);
            if (element == null || !element.IsValidObject)
                return RenderNodeAction.Skip;

            _currentElement = element;
            _currentGuid = element.UniqueId;
            _currentName = element.Name;
            _currentMaterialData = MaterialData.Default;
            _currentMaterialKey = MaterialData.Default.GetKey();
            _elementGeometry = new Dictionary<string, ElementGeometryBuffer>();

            if (element is RevitLinkInstance linkInstance)
                _linkTransform = linkInstance.GetTransform();

            _elementCount++;

            try { _metadataCollector.ExtractElement(element); }
            catch (Exception ex)
            {
                LogError($"ExtractElement failed for {_currentGuid}: {ex.Message}");
            }

            if (_elementCount % 100 == 0)
                _progressIndicator?.Report(_elementCount, _estimatedTotalElements);

            if (_elementCount % 1000 == 0)
            {
                _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] Progress: elements={_elementCount}, polymeshes={_polymeshCount}, failures={_polymeshFailCount}");
                _logWriter.Flush();
            }

            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            if (_currentElement == null || _elementGeometry == null || _elementGeometry.Count == 0)
            {
                _currentElement = null;
                _currentGuid = null;
                return;
            }

            try
            {
                bool hasGeometry = _elementGeometry.Values.Any(b => b.VertexCount > 0);
                if (hasGeometry)
                    _gltfBuilder.AddElement(_currentGuid, _currentName, _elementGeometry);
            }
            catch (Exception ex)
            {
                LogError($"AddElement failed for {_currentGuid}: [{ex.GetType().Name}] {ex.Message}");
            }

            _elementGeometry = null;
            _currentElement = null;
            _currentGuid = null;
            _currentName = null;
        }

        public void OnMaterial(MaterialNode node)
        {
            _currentMaterialData = ExtractMaterialData(node);
            _currentMaterialKey = _currentMaterialData.GetKey();
        }

        public void OnPolymesh(PolymeshTopology polymesh)
        {
            if (string.IsNullOrEmpty(_currentGuid))
                return;

            _polymeshCount++;

            try
            {
                var pts = polymesh.GetPoints();
                var facets = polymesh.GetFacets();
                if (pts.Count == 0 || facets.Count == 0) return;

                var matKey = _currentMaterialKey ?? MaterialData.Default.GetKey();
                if (!_elementGeometry.TryGetValue(matKey, out var buf))
                {
                    buf = new ElementGeometryBuffer { MaterialData = _currentMaterialData ?? MaterialData.Default };
                    _elementGeometry[matKey] = buf;
                }

                var transform = CurrentTransform;

                var worldPts = new XYZ[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                    worldPts[i] = transform.OfPoint(pts[i]);

                IList<XYZ> rawNormals = null;
                var dist = DistributionOfNormals.OnEachFacet;
                try
                {
                    dist = polymesh.DistributionOfNormals;
                    rawNormals = polymesh.GetNormals();
                }
                catch { }

                IList<UV> rawUVs = null;
                try { rawUVs = polymesh.GetUVs(); } catch { }

                int baseVertex = buf.VertexCount;

                if (rawNormals != null && dist == DistributionOfNormals.AtEachPoint)
                {
                    for (int i = 0; i < worldPts.Length; i++)
                    {
                        buf.AddPosition(worldPts[i]);

                        if (i < rawNormals.Count)
                        {
                            var tn = transform.OfVector(rawNormals[i]);
                            buf.AddNormal(tn.GetLength() > 1e-10 ? tn.Normalize() : XYZ.BasisZ);
                        }
                        else
                        {
                            buf.AddNormal(XYZ.BasisZ);
                        }

                        if (rawUVs != null && i < rawUVs.Count)
                            buf.AddUV(rawUVs[i]);
                    }

                    foreach (var facet in facets)
                    {
                        buf.Indices.Add(baseVertex + facet.V1);
                        buf.Indices.Add(baseVertex + facet.V2);
                        buf.Indices.Add(baseVertex + facet.V3);
                    }
                }
                else
                {
                    for (int fi = 0; fi < facets.Count; fi++)
                    {
                        var facet = facets[fi];
                        var p0 = worldPts[facet.V1];
                        var p1 = worldPts[facet.V2];
                        var p2 = worldPts[facet.V3];

                        buf.AddPosition(p0);
                        buf.AddPosition(p1);
                        buf.AddPosition(p2);

                        XYZ faceNormal;
                        if (rawNormals == null)
                        {
                            faceNormal = ComputeFaceNormal(p0, p1, p2);
                        }
                        else if (dist == DistributionOfNormals.OnePerFace && rawNormals.Count > 0)
                        {
                            var tn = transform.OfVector(rawNormals[0]);
                            faceNormal = tn.GetLength() > 1e-10 ? tn.Normalize() : ComputeFaceNormal(p0, p1, p2);
                        }
                        else if (dist == DistributionOfNormals.OnEachFacet && fi < rawNormals.Count)
                        {
                            var tn = transform.OfVector(rawNormals[fi]);
                            faceNormal = tn.GetLength() > 1e-10 ? tn.Normalize() : ComputeFaceNormal(p0, p1, p2);
                        }
                        else
                        {
                            faceNormal = ComputeFaceNormal(p0, p1, p2);
                        }

                        buf.AddNormal(faceNormal);
                        buf.AddNormal(faceNormal);
                        buf.AddNormal(faceNormal);

                        if (rawUVs != null && rawUVs.Count > 0)
                        {
                            buf.AddUV(facet.V1 < rawUVs.Count ? rawUVs[facet.V1] : new UV(0, 0));
                            buf.AddUV(facet.V2 < rawUVs.Count ? rawUVs[facet.V2] : new UV(0, 0));
                            buf.AddUV(facet.V3 < rawUVs.Count ? rawUVs[facet.V3] : new UV(0, 0));
                        }

                        int bi = baseVertex + fi * 3;
                        buf.Indices.Add(bi);
                        buf.Indices.Add(bi + 1);
                        buf.Indices.Add(bi + 2);
                    }
                }
            }
            catch (Exception ex)
            {
                _polymeshFailCount++;
                if (_polymeshFailCount <= 100 || _polymeshFailCount % 1000 == 0)
                {
                    var stLines = ex.StackTrace?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var firstLine = stLines != null && stLines.Length > 0 ? stLines[0].Trim() : "N/A";
                    LogError($"OnPolymesh failed for {_currentGuid}: [{ex.GetType().Name}] {ex.Message}");
                    LogError($"  at: {firstLine}");
                    if (_polymeshFailCount % 100 == 0) _logWriter.Flush();
                }
            }
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            try
            {
                _transformStack.Push(CurrentTransform.Multiply(node.GetTransform()));
            }
            catch (Exception ex)
            {
                LogError($"OnInstanceBegin failed: {ex.Message}");
                _transformStack.Push(CurrentTransform);
            }
            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node)
        {
            if (_transformStack.Count > 1)
                _transformStack.Pop();
        }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            _isLink = true;
            _currentDoc = node.GetDocument();

            if (_linkTransform != null)
                _transformStack.Push(CurrentTransform.Multiply(_linkTransform));
            else
                _transformStack.Push(CurrentTransform);

            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
            _isLink = false;
            if (_transformStack.Count > 1)
                _transformStack.Pop();
            _currentDoc = _hostDoc;
        }

        public void OnRPC(RPCNode node) { }
        public void OnLight(LightNode node) { }
        public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
        public void OnFaceEnd(FaceNode node) { }

        #endregion

        #region Serialization

        public void Serialize()
        {
            LogError("Writing GLB and Parquet...");

            _gltfBuilder.SerializeToGlb();
            LogError("GLB written.");

            _metadataCollector.SerializeToParquet(_outputPath);

            var parquetGuids = _metadataCollector.GetAllGuids();
            var gltfGuids = _gltfBuilder.GetAllGuids();

            LogError($"Parquet written. records={parquetGuids.Count}, geometry_nodes={gltfGuids.Count}, failures={_polymeshFailCount}/{_polymeshCount}");

            if (gltfGuids.Count > 0 && !gltfGuids.SetEquals(parquetGuids))
            {
                var onlyInGlb = new HashSet<string>(gltfGuids);
                onlyInGlb.ExceptWith(parquetGuids);
                var onlyInPq = new HashSet<string>(parquetGuids);
                onlyInPq.ExceptWith(gltfGuids);
                LogError($"GUID coverage: only_in_GLB={onlyInGlb.Count}, only_in_Parquet={onlyInPq.Count}");
            }

            _logWriter.Flush();
        }

        #endregion

        #region Material Extraction

        private MaterialData ExtractMaterialData(MaterialNode materialNode)
        {
            if (materialNode == null)
                return MaterialData.Default;

            try
            {
                float r = materialNode.Color.Red / 255f;
                float g = materialNode.Color.Green / 255f;
                float b = materialNode.Color.Blue / 255f;
                float transparency = (float)materialNode.Transparency;
                float metallic = 0f;
                float roughness = 1f - (materialNode.Smoothness / 100f);

                try
                {
                    if (materialNode.MaterialId != ElementId.InvalidElementId)
                    {
                        var material = _currentDoc.GetElement(materialNode.MaterialId) as Material;
                        if (material != null && material.IsValidObject)
                        {
                            var appAssetId = material.AppearanceAssetId;
                            if (appAssetId != ElementId.InvalidElementId)
                            {
                                var appElement = _currentDoc.GetElement(appAssetId) as AppearanceAssetElement;
                                if (appElement != null)
                                {
                                    var asset = appElement.GetRenderingAsset();
                                    if (asset != null)
                                    {
                                        var appColor = GetAppearanceColor(asset);
                                        if (appColor != null)
                                        {
                                            r = appColor[0];
                                            g = appColor[1];
                                            b = appColor[2];
                                        }

                                        ExtractPbrProperties(asset, ref metallic, ref roughness);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                roughness = Math.Max(0f, Math.Min(1f, roughness));

                return new MaterialData
                {
                    Color = new float[] { r, g, b },
                    Transparency = transparency,
                    Metallic = metallic,
                    Roughness = roughness
                };
            }
            catch
            {
                return MaterialData.Default;
            }
        }

        private float[] GetAppearanceColor(Asset asset)
        {
            try
            {
                var baseSchema = asset.FindByName("BaseSchema") as AssetPropertyString;
                if (baseSchema == null || string.IsNullOrEmpty(baseSchema.Value))
                    return null;

                if (!ColorPropertyMap.TryGetValue(baseSchema.Value, out var propName))
                    return null;

                var colorProp = asset.FindByName(propName) as AssetPropertyDoubleArray4d;
                if (colorProp == null) return null;

                var values = colorProp.GetValueAsDoubles();
                if (values == null || values.Count < 3) return null;

                return new float[] { (float)values[0], (float)values[1], (float)values[2] };
            }
            catch
            {
                return null;
            }
        }

        private void ExtractPbrProperties(Asset asset, ref float metallic, ref float roughness)
        {
            try
            {
                var metalProp = asset.FindByName("generic_is_metal");
                if (metalProp is AssetPropertyBoolean boolProp)
                    metallic = boolProp.Value ? 1f : 0f;
                else if (metalProp is AssetPropertyInteger intProp)
                    metallic = intProp.Value > 0 ? 1f : 0f;
            }
            catch { }

            try
            {
                var glossProp = asset.FindByName("generic_glossiness") as AssetPropertyDouble;
                if (glossProp != null)
                    roughness = 1f - (float)glossProp.Value;
            }
            catch { }
        }

        #endregion

        #region Helpers

        private static XYZ ComputeFaceNormal(XYZ p0, XYZ p1, XYZ p2)
        {
            var edge1 = p1 - p0;
            var edge2 = p2 - p0;
            var cross = edge1.CrossProduct(edge2);
            return cross.GetLength() > 1e-10 ? cross.Normalize() : XYZ.BasisZ;
        }

        public void LogError(string message)
        {
            if (_logClosed) return;
            _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        public void LogTiming(double exportSeconds, double serializeSeconds)
        {
            if (_logClosed) return;
            _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] Export() took {exportSeconds:F1}s, Serialize() took {serializeSeconds:F1}s. elements={_elementCount}, polymeshes={_polymeshCount}");
            _logWriter.Flush();
        }

        public void CloseLog()
        {
            if (_logClosed) return;
            try { _logWriter?.Close(); } catch { }
            _logClosed = true;
        }

        public List<DTElementRecord> GetExtractedRecords()
        {
            return _metadataCollector.GetAllRecords();
        }

        #endregion
    }

    public class MaterialData
    {
        public float[] Color { get; set; }
        public float Transparency { get; set; }
        public float Metallic { get; set; }
        public float Roughness { get; set; }

        public string GetKey()
        {
            return $"{Color[0]:F3}_{Color[1]:F3}_{Color[2]:F3}_{Transparency:F3}_{Metallic:F2}_{Roughness:F2}";
        }

        public static MaterialData Default => new MaterialData
        {
            Color = new float[] { 0.8f, 0.8f, 0.8f },
            Transparency = 0f,
            Metallic = 0f,
            Roughness = 0.5f
        };
    }

    public class ElementGeometryBuffer
    {
        public readonly List<float> Positions = new List<float>();
        public readonly List<float> Normals = new List<float>();
        public readonly List<float> UVs = new List<float>();
        public readonly List<int> Indices = new List<int>();
        public MaterialData MaterialData;

        public int VertexCount => Positions.Count / 3;
        public bool HasUVs => UVs.Count > 0;

        public void AddPosition(XYZ p)
        {
            Positions.Add((float)p.X);
            Positions.Add((float)p.Y);
            Positions.Add((float)p.Z);
        }

        public void AddNormal(XYZ n)
        {
            Normals.Add((float)n.X);
            Normals.Add((float)n.Y);
            Normals.Add((float)n.Z);
        }

        public void AddUV(UV uv)
        {
            UVs.Add((float)uv.U);
            UVs.Add((float)(1.0 - uv.V));
        }

        public Vector3[] GetPositionArray()
        {
            var arr = new Vector3[VertexCount];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = new Vector3(Positions[i * 3], Positions[i * 3 + 1], Positions[i * 3 + 2]);
            return arr;
        }

        public Vector3[] GetNormalArray()
        {
            int count = Normals.Count / 3;
            var arr = new Vector3[count];
            for (int i = 0; i < count; i++)
                arr[i] = new Vector3(Normals[i * 3], Normals[i * 3 + 1], Normals[i * 3 + 2]);
            return arr;
        }

        public Vector2[] GetUVArray()
        {
            if (UVs.Count == 0) return null;
            int count = UVs.Count / 2;
            var arr = new Vector2[count];
            for (int i = 0; i < count; i++)
                arr[i] = new Vector2(UVs[i * 2], UVs[i * 2 + 1]);
            return arr;
        }
    }
}
