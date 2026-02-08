using System;

namespace DTExtractor.Models
{
    /// <summary>
    /// Complete parameter record preserving all Revit metadata
    /// </summary>
    public class DTParameterRecord
    {
        // Identity
        public string Name { get; set; }
        public ParameterSource Source { get; set; }

        // Value
        public string StorageType { get; set; } // Double, Integer, String, ElementId
        public object Value { get; set; } // Internal unit (feet, radians)
        public string DisplayValue { get; set; } // User-facing formatted value

        // Shared Parameter Identity
        public bool IsShared { get; set; }
        public string SharedGuid { get; set; }

        // Classification
        public string Group { get; set; } // BuiltInParameterGroup
        public string UnitType { get; set; } // ForgeTypeId for unit conversion

        // Metadata
        public bool IsReadOnly { get; set; }
        public bool HasValue { get; set; }

        // For ElementId references
        public string ReferencedElementGuid { get; set; }
    }
}
