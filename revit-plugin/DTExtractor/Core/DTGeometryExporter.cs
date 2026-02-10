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
            // Prism (newer) schemas
            { "PrismGenericSchema", "generic_diffuse" },
            { "PrismOpaqueSchema", "opaque_albedo" },
            { "PrismMetalSchema", "adsklib_Metal_F0" },
            { "PrismLayeredSchema", "adsklib_Layered_Diffuse" },
            { "PrismMasonryCMUSchema", "masonrycmu_color" },
            { "PrismWoodSchema", "wood_color" },
            { "PrismGlazingSchema", "glazing_transmittance_color" },
            { "PrismStoneSchema", "stone_color" },
            { "PrismTransparentSchema", "transparent_color" },
            { "PrismMirrorSchema", "mirror_tintcolor" },
            { "PrismSoftwoodSchema", "softwood_color" },
            // Classic schemas
            { "GenericSchema", "generic_diffuse" },
            { "ConcreteSchema", "concrete_color" },
            { "WallPaintSchema", "wallpaint_color" },
            { "PlasticVinylSchema", "plasticvinyl_color" },
            { "MetallicPaintSchema", "metallicpaint_base_color" },
            { "CeramicSchema", "ceramic_color" },
            { "MetalSchema", "metal_color" },
            { "HardwoodSchema", "hardwood_color" },
            { "MasonryCMUSchema", "masonrycmu_color" },
            { "GlazingSchema", "glazing_transmittance_color" },
            { "WoodSchema", "wood_color" },
            { "StoneSchema", "stone_color" },
            { "SolidGlassSchema", "solidglass_transmittance_custom_color" },
            { "MirrorSchema", "mirror_tintcolor" },
            { "WaterSchema", "water_tint_color" },
            { "SoftwoodSchema", "softwood_color" },
        };

        private static readonly string[] FallbackColorPropertyNames = new string[]
        {
            "generic_diffuse",
            "opaque_albedo",
            "common_Tint/Shade_Color",
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

                foreach (var facet in facets)
                {
                    buf.AddVertexDedup(worldPts[facet.V1]);
                    buf.AddVertexDedup(worldPts[facet.V2]);
                    buf.AddVertexDedup(worldPts[facet.V3]);
                }
            }
            catch (Exception ex)
            {
                _polymeshFailCount++;
                if (_polymeshFailCount <= 100 || _polymeshFailCount % 1000 == 0)
                {
                    LogError($"OnPolymesh failed for {_currentGuid}: [{ex.GetType().Name}] {ex.Message}");
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
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                return new MaterialData
                {
                    Color = new float[] { r, g, b },
                    Transparency = transparency,
                    Metallic = 0f,
                    Roughness = transparency > 0 ? 0.5f : 1f
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
                string schemaName = null;
                var baseSchema = asset.FindByName("BaseSchema") as AssetPropertyString;
                if (baseSchema != null && !string.IsNullOrEmpty(baseSchema.Value))
                    schemaName = baseSchema.Value;

                float[] schemaColor = null;

                if (schemaName != null && ColorPropertyMap.TryGetValue(schemaName, out var propName))
                {
                    var diffuseProp = asset.FindByName(propName);
                    bool hasConnectedTexture = diffuseProp != null &&
                                               diffuseProp.NumberOfConnectedProperties > 0;

                    if (hasConnectedTexture)
                    {
                        var tint = TryGetTintColor(asset);
                        if (tint != null) return tint;
                    }

                    schemaColor = TryReadColorProperty(asset, propName);
                    if (schemaColor != null && !IsNearWhite(schemaColor))
                        return schemaColor;
                }

                foreach (var fallbackName in FallbackColorPropertyNames)
                {
                    var result = TryReadColorProperty(asset, fallbackName);
                    if (result != null && !IsNearWhite(result))
                        return result;
                }

                for (int i = 0; i < asset.Size; i++)
                {
                    try
                    {
                        var prop = asset[i];
                        if (prop is AssetPropertyDoubleArray4d colorProp)
                        {
                            var name = prop.Name.ToLowerInvariant();
                            if (name.Contains("color") || name.Contains("diffuse") ||
                                name.Contains("albedo") || name.Contains("tint"))
                            {
                                var values = colorProp.GetValueAsDoubles();
                                if (values != null && values.Count >= 3)
                                {
                                    float cr = (float)values[0], cg = (float)values[1], cb = (float)values[2];
                                    if (!IsNearWhite(cr, cg, cb))
                                        return new float[] { cr, cg, cb };
                                }
                            }
                        }
                    }
                    catch { }
                }

                return schemaColor;
            }
            catch
            {
                return null;
            }
        }

        private static float[] TryGetTintColor(Asset asset)
        {
            try
            {
                var tintToggle = asset.FindByName("common_Tint_toggle") as AssetPropertyBoolean;
                if (tintToggle != null && !tintToggle.Value)
                    return null;

                var tintProp = asset.FindByName("common_Tint/Shade_Color") as AssetPropertyDoubleArray4d;
                if (tintProp == null) return null;

                var values = tintProp.GetValueAsDoubles();
                if (values == null || values.Count < 3) return null;

                float r = (float)values[0], g = (float)values[1], b = (float)values[2];
                if (IsNearWhite(r, g, b))
                    return null;

                return new float[] { r, g, b };
            }
            catch
            {
                return null;
            }
        }

        private static float[] TryReadColorProperty(Asset asset, string propertyName)
        {
            try
            {
                var colorProp = asset.FindByName(propertyName) as AssetPropertyDoubleArray4d;
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

        private static bool IsNearWhite(float[] c) => c[0] > 0.95f && c[1] > 0.95f && c[2] > 0.95f;
        private static bool IsNearWhite(float r, float g, float b) => r > 0.95f && g > 0.95f && b > 0.95f;

        #endregion

        #region Helpers

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
            Roughness = 1f
        };
    }

    public class ElementGeometryBuffer
    {
        public readonly List<float> Positions = new List<float>();
        public readonly List<int> Indices = new List<int>();
        public MaterialData MaterialData;

        private readonly Dictionary<(float, float, float), int> _vertexLookup =
            new Dictionary<(float, float, float), int>();

        public int VertexCount => Positions.Count / 3;

        public void AddVertexDedup(XYZ p)
        {
            float x = (float)p.X;
            float y = (float)p.Y;
            float z = (float)p.Z;
            var key = (x, y, z);

            if (_vertexLookup.TryGetValue(key, out int existingIndex))
            {
                Indices.Add(existingIndex);
                return;
            }

            int newIndex = VertexCount;
            _vertexLookup[key] = newIndex;
            Positions.Add(x);
            Positions.Add(y);
            Positions.Add(z);
            Indices.Add(newIndex);
        }

        public Vector3[] GetPositionArray()
        {
            var arr = new Vector3[VertexCount];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = new Vector3(Positions[i * 3], Positions[i * 3 + 1], Positions[i * 3 + 2]);
            return arr;
        }
    }
}
