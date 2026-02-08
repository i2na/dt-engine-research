using System;
using System.Collections.Generic;

namespace DTExtractor.Models
{
    /// <summary>
    /// Complete BIM element record with 7 parameter types
    /// </summary>
    public class DTElementRecord
    {
        // Core Identity
        public string Guid { get; set; }
        public int ElementId { get; set; }

        // Classification
        public string Category { get; set; }
        public int? CategoryId { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }

        // Spatial Context
        public string LevelName { get; set; }
        public string PhaseName { get; set; }

        // Geometry Metadata
        public double[] BoundingBox { get; set; } // [minX, minY, minZ, maxX, maxY, maxZ]
        public double Volume { get; set; }
        public double Area { get; set; }

        // 7 Types of Parameters
        public List<DTParameterRecord> InstanceParameters { get; set; } = new List<DTParameterRecord>();
        public List<DTParameterRecord> TypeParameters { get; set; } = new List<DTParameterRecord>();
        public List<DTParameterRecord> BuiltInParameters { get; set; } = new List<DTParameterRecord>();
        public List<DTParameterRecord> SharedParameters { get; set; } = new List<DTParameterRecord>();
        public List<DTParameterRecord> ProjectParameters { get; set; } = new List<DTParameterRecord>();
        public List<DTParameterRecord> GlobalParameters { get; set; } = new List<DTParameterRecord>();
        public List<DTParameterRecord> FamilyParameters { get; set; } = new List<DTParameterRecord>();

        // Metadata
        public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;
        public int ModelVersion { get; set; }
    }

    public enum ParameterSource
    {
        Instance,
        Type,
        BuiltIn,
        Shared,
        Project,
        Global,
        Family
    }
}
