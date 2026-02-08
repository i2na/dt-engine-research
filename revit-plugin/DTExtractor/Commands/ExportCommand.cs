using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using DTExtractor.Core;

namespace DTExtractor.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            if (doc == null)
            {
                TaskDialog.Show("Error", "No active document found.");
                return Result.Failed;
            }

            string outputPath = null;
            try
            {
                using (var saveDialog = new SaveFileDialog())
                {
                    saveDialog.Title = "Export to DT Engine";
                    saveDialog.Filter = "GLB files (*.glb)|*.glb";
                    saveDialog.FileName = "model.glb";

                    if (saveDialog.ShowDialog() != DialogResult.OK)
                        return Result.Cancelled;

                    outputPath = saveDialog.FileName;

                    var view3D = GetExportView(doc, uiDoc);
                    if (view3D == null)
                    {
                        TaskDialog.Show("Error", "No suitable 3D view found. Please create a 3D view first.");
                        return Result.Failed;
                    }

                    var exporter = new DTGeometryExporter(doc, outputPath);
                    var customExporter = new CustomExporter(doc, exporter);
                    customExporter.IncludeGeometricObjects = true;
                    customExporter.ShouldStopOnError = false;

                    var swExport = Stopwatch.StartNew();
                    customExporter.Export(view3D);
                    swExport.Stop();

                    var swSerialize = Stopwatch.StartNew();
                    exporter.Serialize();
                    swSerialize.Stop();

                    var exportLogPath = Path.ChangeExtension(outputPath, ".export-log.txt");
                    try
                    {
                        File.AppendAllText(exportLogPath, $"[{DateTime.Now:HH:mm:ss}] Export() took {swExport.Elapsed.TotalSeconds:F1}s, Serialize() took {swSerialize.Elapsed.TotalSeconds:F1}s. elements={exporter.ElementCount}, polymeshes={exporter.PolymeshCount}\r\n");
                    }
                    catch { }

                    // Success message
                    var glbPath = Path.ChangeExtension(outputPath, ".glb");
                    var parquetPath = Path.ChangeExtension(outputPath, ".parquet");

                    var glbSize = new FileInfo(glbPath).Length / 1024.0 / 1024.0;
                    var parquetSize = new FileInfo(parquetPath).Length / 1024.0 / 1024.0;

                    TaskDialog.Show(
                        "Export Complete",
                        $"Files exported successfully:\n\n" +
                        $"Geometry: {Path.GetFileName(glbPath)} ({glbSize:F2} MB)\n" +
                        $"Metadata: {Path.GetFileName(parquetPath)} ({parquetSize:F2} MB)\n" +
                        $"Elements: {exporter.ElementCount}, Polymeshes: {exporter.PolymeshCount}\n\n" +
                        $"Output: {Path.GetDirectoryName(outputPath)}\n" +
                        $"Diagnostic log: {Path.GetFileName(exportLogPath)}");

                    return Result.Succeeded;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                var errorLogPath = outputPath != null ? Path.ChangeExtension(outputPath, ".export-log.txt") : null;
                var logHint = !string.IsNullOrEmpty(errorLogPath) && File.Exists(errorLogPath)
                    ? $"\n\nCheck {errorLogPath} for progress and errors."
                    : "";
                TaskDialog.Show("Export Error", $"An error occurred during export:\n\n{ex.Message}\n\n{ex.StackTrace}{logHint}");
                return Result.Failed;
            }
        }

        private View3D GetExportView(Document doc, UIDocument uiDoc)
        {
            var activeView = uiDoc.ActiveView as View3D;
            if (activeView != null && !activeView.IsTemplate)
                return activeView;

            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D));

            View3D defaultView = null;
            View3D anyView = null;

            foreach (View3D view in collector)
            {
                if (view.IsTemplate)
                    continue;

                if (view.Name.StartsWith("{3D"))
                    defaultView = view;

                if (anyView == null)
                    anyView = view;
            }

            return defaultView ?? anyView;
        }
    }
}
