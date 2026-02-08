using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using DTExtractor.Models;

namespace DTExtractor.Core
{
    /// <summary>
    /// Writes BIM metadata to Apache Parquet format
    /// Optimized for DuckDB-WASM columnar queries
    /// </summary>
    public class DTParquetWriter
    {
        private readonly string _outputPath;

        public DTParquetWriter(string outputPath)
        {
            _outputPath = outputPath;
        }

        public void Write(List<DTElementRecord> records)
        {
            if (records == null || records.Count == 0)
                return;

            // Define Parquet schema
            var schema = new ParquetSchema(
                new DataField<string>("guid"),
                new DataField<int>("element_id"),
                new DataField<string>("category"),
                new DataField<int?>("category_id"),
                new DataField<string>("family_name"),
                new DataField<string>("type_name"),
                new DataField<string>("level_name"),
                new DataField<string>("phase_name"),
                new DataField<double>("volume"),
                new DataField<double>("area"),
                new DataField<string>("bbox_min_x"),
                new DataField<string>("bbox_min_y"),
                new DataField<string>("bbox_min_z"),
                new DataField<string>("bbox_max_x"),
                new DataField<string>("bbox_max_y"),
                new DataField<string>("bbox_max_z"),
                new DataField<string>("instance_parameters"), // JSON string
                new DataField<string>("type_parameters"),     // JSON string
                new DataField<string>("builtin_parameters"),  // JSON string
                new DataField<DateTime>("extracted_at")
            );

            // Prepare data columns
            var guids = records.Select(r => r.Guid).ToArray();
            var elementIds = records.Select(r => r.ElementId).ToArray();
            var categories = records.Select(r => r.Category ?? "").ToArray();
            var categoryIds = records.Select(r => r.CategoryId).ToArray();
            var familyNames = records.Select(r => r.FamilyName ?? "").ToArray();
            var typeNames = records.Select(r => r.TypeName ?? "").ToArray();
            var levelNames = records.Select(r => r.LevelName ?? "").ToArray();
            var phaseNames = records.Select(r => r.PhaseName ?? "").ToArray();
            var volumes = records.Select(r => r.Volume).ToArray();
            var areas = records.Select(r => r.Area).ToArray();

            var bboxMinX = records.Select(r => r.BoundingBox[0].ToString("F3")).ToArray();
            var bboxMinY = records.Select(r => r.BoundingBox[1].ToString("F3")).ToArray();
            var bboxMinZ = records.Select(r => r.BoundingBox[2].ToString("F3")).ToArray();
            var bboxMaxX = records.Select(r => r.BoundingBox[3].ToString("F3")).ToArray();
            var bboxMaxY = records.Select(r => r.BoundingBox[4].ToString("F3")).ToArray();
            var bboxMaxZ = records.Select(r => r.BoundingBox[5].ToString("F3")).ToArray();

            // Serialize parameters as JSON
            var instanceParams = records.Select(r => SerializeParameters(r.InstanceParameters)).ToArray();
            var typeParams = records.Select(r => SerializeParameters(r.TypeParameters)).ToArray();
            var builtinParams = records.Select(r => SerializeParameters(r.BuiltInParameters)).ToArray();

            var extractedAt = records.Select(r => r.ExtractedAt).ToArray();

            Task.Run(() => WriteAsync(schema, guids, elementIds, categories, categoryIds, familyNames, typeNames,
                levelNames, phaseNames, volumes, areas, bboxMinX, bboxMinY, bboxMinZ, bboxMaxX, bboxMaxY, bboxMaxZ,
                instanceParams, typeParams, builtinParams, extractedAt)).GetAwaiter().GetResult();
        }

        private async Task WriteAsync(ParquetSchema schema,
            string[] guids, int[] elementIds, string[] categories, int?[] categoryIds, string[] familyNames, string[] typeNames,
            string[] levelNames, string[] phaseNames, double[] volumes, double[] areas,
            string[] bboxMinX, string[] bboxMinY, string[] bboxMinZ, string[] bboxMaxX, string[] bboxMaxY, string[] bboxMaxZ,
            string[] instanceParams, string[] typeParams, string[] builtinParams, DateTime[] extractedAt)
        {
            using (var stream = File.Create(_outputPath))
            using (var writer = await ParquetWriter.CreateAsync(schema, stream))
            {
                writer.CompressionMethod = CompressionMethod.Gzip;
                writer.CompressionLevel = System.IO.Compression.CompressionLevel.Optimal;
                using (var groupWriter = writer.CreateRowGroup())
                {
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[0], guids));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[1], elementIds));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[2], categories));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[3], categoryIds));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[4], familyNames));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[5], typeNames));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[6], levelNames));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[7], phaseNames));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[8], volumes));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[9], areas));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[10], bboxMinX));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[11], bboxMinY));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[12], bboxMinZ));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[13], bboxMaxX));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[14], bboxMaxY));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[15], bboxMaxZ));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[16], instanceParams));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[17], typeParams));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[18], builtinParams));
                    await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[19], extractedAt));
                }
            }
        }

        private string SerializeParameters(List<DTParameterRecord> parameters)
        {
            if (parameters == null || parameters.Count == 0)
                return "{}";

            var dict = new Dictionary<string, object>();
            foreach (var param in parameters)
            {
                dict[param.Name] = new
                {
                    value = param.Value,
                    displayValue = param.DisplayValue,
                    storageType = param.StorageType,
                    isShared = param.IsShared,
                    sharedGuid = param.SharedGuid,
                    isReadOnly = param.IsReadOnly
                };
            }

            return Newtonsoft.Json.JsonConvert.SerializeObject(dict);
        }
    }
}
