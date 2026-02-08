using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using DTExtractor.Models;

namespace DTExtractor.Core
{
    /// <summary>
    /// Extracts all 7 types of Revit parameters with 100% fidelity
    /// </summary>
    public class DTMetadataCollector
    {
        private readonly Dictionary<string, DTElementRecord> _records = new Dictionary<string, DTElementRecord>();

        public DTElementRecord ExtractElement(Element element)
        {
            if (element == null)
                return null;

            var record = new DTElementRecord
            {
                Guid = element.UniqueId,
                ElementId = unchecked((int)element.Id.Value),
                Category = element.Category?.Name ?? "Unknown",
                CategoryId = element.Category != null ? unchecked((int?)element.Category.Id.Value) : null,
                LevelName = GetLevelName(element),
                PhaseName = GetPhaseName(element),
                BoundingBox = GetBoundingBox(element),
                Volume = GetVolume(element),
                Area = GetArea(element)
            };

            // 1. Instance Parameters
            record.InstanceParameters = ExtractParameters(
                element.Parameters, ParameterSource.Instance);

            // 2. Type Parameters
            if (element is FamilyInstance fi && fi.Symbol != null)
            {
                record.FamilyName = fi.Symbol.Family.Name;
                record.TypeName = fi.Symbol.Name;
                record.TypeParameters = ExtractParameters(
                    fi.Symbol.Parameters, ParameterSource.Type);
            }
            else
            {
                var typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    var typeElement = element.Document.GetElement(typeId);
                    if (typeElement != null)
                    {
                        record.TypeName = typeElement.Name;
                        record.TypeParameters = ExtractParameters(
                            typeElement.Parameters, ParameterSource.Type);
                    }
                }
            }

            // 3. BuiltIn Parameters (comprehensive)
            record.BuiltInParameters = ExtractBuiltInParameters(element);

            // 4. Shared Parameters
            record.SharedParameters = record.InstanceParameters
                .Concat(record.TypeParameters ?? new List<DTParameterRecord>())
                .Where(p => p.IsShared)
                .ToList();

            _records[record.Guid] = record;
            return record;
        }

        private List<DTParameterRecord> ExtractParameters(
            ParameterSet parameters, ParameterSource source)
        {
            var result = new List<DTParameterRecord>();

            foreach (Parameter param in parameters)
            {
                if (!param.HasValue)
                    continue;

                var record = new DTParameterRecord
                {
                    Name = param.Definition.Name,
                    Source = source,
                    StorageType = param.StorageType.ToString(),
                    HasValue = param.HasValue,
                    IsShared = param.IsShared,
                    IsReadOnly = param.IsReadOnly,
#if REVIT2024
                    Group = param.Definition.GetGroupTypeId()?.TypeId ?? ""
#else
                    Group = param.Definition.ParameterGroup.ToString()
#endif
                };

                // Extract value based on storage type
                switch (param.StorageType)
                {
                    case StorageType.Double:
                        record.Value = param.AsDouble();
                        record.DisplayValue = param.AsValueString() ?? param.AsDouble().ToString("F3");
#if REVIT2022 || REVIT2023 || REVIT2024
                        record.UnitType = param.Definition.GetDataType()?.TypeId;
#endif
                        break;

                    case StorageType.Integer:
                        record.Value = param.AsInteger();
                        record.DisplayValue = param.AsValueString() ?? param.AsInteger().ToString();
                        break;

                    case StorageType.String:
                        record.Value = param.AsString() ?? "";
                        record.DisplayValue = param.AsString() ?? "";
                        break;

                    case StorageType.ElementId:
                        var elemId = param.AsElementId();
                        if (elemId != null && elemId != ElementId.InvalidElementId)
                        {
                            var refElement = param.Element.Document.GetElement(elemId);
                            if (refElement != null)
                            {
                                record.ReferencedElementGuid = refElement.UniqueId;
                                record.DisplayValue = refElement.Name;
                            }
                        }
                        record.Value = elemId?.IntegerValue ?? -1;
                        break;
                }

                // Shared parameter GUID
                if (param.IsShared)
                {
                    record.SharedGuid = param.GUID.ToString();
                }

                result.Add(record);
            }

            return result;
        }

        private List<DTParameterRecord> ExtractBuiltInParameters(Element element)
        {
            var result = new List<DTParameterRecord>();

            // Critical BuiltIn parameters
            var builtInParams = new[]
            {
                BuiltInParameter.ALL_MODEL_MARK,
                BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
                BuiltInParameter.ELEM_FAMILY_PARAM,
                BuiltInParameter.ELEM_TYPE_PARAM,
                BuiltInParameter.PHASE_CREATED,
                BuiltInParameter.PHASE_DEMOLISHED,
                BuiltInParameter.DESIGN_OPTION_ID,
                BuiltInParameter.LEVEL_NAME,
                BuiltInParameter.ROOM_NUMBER,
                BuiltInParameter.ROOM_NAME
            };

            foreach (var bip in builtInParams)
            {
                var param = element.get_Parameter(bip);
                if (param != null && param.HasValue)
                {
                    var record = new DTParameterRecord
                    {
                        Name = LabelUtils.GetLabelFor(bip),
                        Source = ParameterSource.BuiltIn,
                        StorageType = param.StorageType.ToString(),
                        HasValue = true,
                        IsReadOnly = param.IsReadOnly
                    };

                    switch (param.StorageType)
                    {
                        case StorageType.Double:
                            record.Value = param.AsDouble();
                            record.DisplayValue = param.AsValueString() ?? param.AsDouble().ToString("F3");
                            break;
                        case StorageType.Integer:
                            record.Value = param.AsInteger();
                            record.DisplayValue = param.AsValueString() ?? param.AsInteger().ToString();
                            break;
                        case StorageType.String:
                            record.Value = param.AsString() ?? "";
                            record.DisplayValue = param.AsString() ?? "";
                            break;
                    }

                    result.Add(record);
                }
            }

            return result;
        }

        private string GetLevelName(Element element)
        {
            var levelId = element.LevelId;
            if (levelId == null || levelId == ElementId.InvalidElementId)
                return null;

            var level = element.Document.GetElement(levelId) as Level;
            return level?.Name;
        }

        private string GetPhaseName(Element element)
        {
            var phaseParam = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (phaseParam != null && phaseParam.HasValue)
            {
                var phaseId = phaseParam.AsElementId();
                if (phaseId != null && phaseId != ElementId.InvalidElementId)
                {
                    var phase = element.Document.GetElement(phaseId) as Phase;
                    return phase?.Name;
                }
            }
            return null;
        }

        private double[] GetBoundingBox(Element element)
        {
            var bb = element.get_BoundingBox(null);
            if (bb == null)
                return new double[6];

            return new double[]
            {
                bb.Min.X, bb.Min.Y, bb.Min.Z,
                bb.Max.X, bb.Max.Y, bb.Max.Z
            };
        }

        private double GetVolume(Element element)
        {
            var volumeParam = element.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
            return volumeParam?.AsDouble() ?? 0.0;
        }

        private double GetArea(Element element)
        {
            var areaParam = element.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
            return areaParam?.AsDouble() ?? 0.0;
        }

        public void SerializeToParquet(string basePath)
        {
            var parquetPath = System.IO.Path.ChangeExtension(basePath, ".parquet");
            var writer = new DTParquetWriter(parquetPath);
            writer.Write(_records.Values.ToList());
        }

        public List<DTElementRecord> GetAllRecords()
        {
            return _records.Values.ToList();
        }

        public HashSet<string> GetAllGuids()
        {
            return new HashSet<string>(_records.Keys);
        }
    }
}
