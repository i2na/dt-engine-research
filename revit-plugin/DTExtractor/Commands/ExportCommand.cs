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
    [Transaction(TransactionMode.Manual)]
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

                    DTGeometryExporter exporter = null;
                    string exportLogPath = null;

                    var progressDialog = new TaskDialog("Exporting to DT Engine")
                    {
                        MainInstruction = "Exporting geometry and metadata...",
                        MainContent = "This may take a few minutes for large models.",
                        AllowCancellation = false,
                        CommonButtons = TaskDialogCommonButtons.None
                    };

                    // Execute export in transaction
                    using (var tx = new Transaction(doc, "DT Engine Export"))
                    {
                        tx.Start();

                        // Get 3D view for export
                        var view3D = Get3DView(doc);
                        if (view3D == null)
                        {
                            TaskDialog.Show("Error", "No 3D view found. Please create a 3D view first.");
                            return Result.Failed;
                        }

                        exporter = new DTGeometryExporter(doc, outputPath);
                        var customExporter = new CustomExporter(doc, exporter);
                        customExporter.IncludeGeometricObjects = true;
                        customExporter.ShouldStopOnError = false;

                        var swExport = Stopwatch.StartNew();
                        customExporter.Export(view3D);
                        swExport.Stop();

                        var swFinish = Stopwatch.StartNew();
                        exporter.Finish();
                        swFinish.Stop();

                        tx.Commit();

                        exportLogPath = Path.ChangeExtension(outputPath, ".export-log.txt");
                        try
                        {
                            File.AppendAllText(exportLogPath, $"[{DateTime.Now:HH:mm:ss}] Export() took {swExport.Elapsed.TotalSeconds:F1}s, Finish() took {swFinish.Elapsed.TotalSeconds:F1}s. elements={exporter.ElementCount}, polymeshes={exporter.PolymeshCount}\r\n");
                        }
                        catch { }
                    }

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

        private View3D Get3DView(Document doc)
        {
            // Try to find default 3D view
            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D));

            foreach (View3D view in collector)
            {
                if (!view.IsTemplate)
                    return view;
            }

            return null;
        }
    }
}
