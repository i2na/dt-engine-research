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
        private class ExportProgressIndicator : IDTProgressIndicator, IDisposable
        {
            private readonly string _logPath;
            private int _lastReportedPercent = -1;
            private readonly Stopwatch _timer;
            private readonly StreamWriter _logWriter;
            private bool _disposed;

            public ExportProgressIndicator(string logPath)
            {
                _logPath = logPath;
                _timer = Stopwatch.StartNew();
                _logWriter = new StreamWriter(_logPath + ".progress", false);
                _logWriter.AutoFlush = true;
            }

            public void Report(int current, int total)
            {
                if (total <= 0) return;

                int percent = (int)((current * 100.0) / total);
                if (percent != _lastReportedPercent && percent % 5 == 0)
                {
                    _lastReportedPercent = percent;
                    var elapsed = _timer.Elapsed;
                    _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] Progress: {percent}% ({current}/{total}) - Elapsed: {elapsed.TotalSeconds:F1}s");
                }
            }

            public void Close()
            {
                if (_disposed) return;

                var totalTime = _timer.Elapsed.TotalSeconds;
                _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] Export phase ended. Total time: {totalTime:F1}s");

                _logWriter.Close();
                _disposed = true;
            }

            public void Dispose()
            {
                Close();
            }
        }
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
            DTGeometryExporter exporter = null;
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

                    exporter = new DTGeometryExporter(doc, outputPath);
                    var exportLogPath = Path.ChangeExtension(outputPath, ".export-log.txt");
                    bool exportHadError = false;
                    Exception exportException = null;

                    var swExport = Stopwatch.StartNew();
                    using (var progressIndicator = new ExportProgressIndicator(exportLogPath))
                    using (var customExporter = new CustomExporter(doc, exporter))
                    {
                        customExporter.IncludeGeometricObjects = false;
                        customExporter.ShouldStopOnError = false;
                        
                        try
                        {
                            exporter.SetProgressIndicator(progressIndicator);
                            customExporter.Export(view3D);
                        }
                        catch (Autodesk.Revit.Exceptions.InvalidObjectException ioEx)
                        {
                            exportHadError = true;
                            exportException = ioEx;
                            exporter.LogError($"InvalidObjectException during Export: {ioEx.Message}");
                        }
                        catch (Exception ex)
                        {
                            exportHadError = true;
                            exportException = ex;
                            exporter.LogError($"Unexpected exception during Export: [{ex.GetType().Name}] {ex.Message}");
                            exporter.LogError($"StackTrace: {ex.StackTrace}");
                        }
                    }
                    swExport.Stop();

                    exporter.LogError($"Export phase completed. Had error: {exportHadError}, Elements: {exporter.ElementCount}, Polymeshes: {exporter.PolymeshCount}");

                    var swSerialize = Stopwatch.StartNew();
                    try
                    {
                        exporter.LogError("Starting Serialize phase...");
                        exporter.Serialize();
                        swSerialize.Stop();
                        exporter.LogTiming(swExport.Elapsed.TotalSeconds, swSerialize.Elapsed.TotalSeconds);
                    }
                    catch (Exception ex)
                    {
                        swSerialize.Stop();
                        exporter.LogError($"FATAL: Serialize failed: [{ex.GetType().Name}] {ex.Message}");
                        exporter.LogError($"StackTrace: {ex.StackTrace}");
                        
                        TaskDialog.Show(
                            "Serialize Error",
                            $"Failed to write output files:\n\n{ex.Message}\n\nCheck {Path.GetFileName(exportLogPath)} for details.");
                        return Result.Failed;
                    }

                    var glbPath = Path.ChangeExtension(outputPath, ".glb");
                    var parquetPath = Path.ChangeExtension(outputPath, ".parquet");

                    var glbExists = File.Exists(glbPath);
                    var parquetExists = File.Exists(parquetPath);

                    var glbSize = glbExists ? new FileInfo(glbPath).Length / 1024.0 / 1024.0 : 0;
                    var parquetSize = parquetExists ? new FileInfo(parquetPath).Length / 1024.0 / 1024.0 : 0;

                    if (!parquetExists)
                    {
                        TaskDialog.Show(
                            "Export Failed",
                            $"Export completed but no usable geometry was extracted.\n\n" +
                            $"Elements processed: {exporter.ElementCount}\n" +
                            $"Polymesh callbacks: {exporter.PolymeshCount}\n" +
                            $"Polymesh failures: {exporter.PolymeshFailCount}\n\n" +
                            $"This typically occurs when the model contains elements with\n" +
                            $"invalid or deleted object references.\n\n" +
                            $"Diagnostic log: {Path.GetFileName(exportLogPath)}");

                        return Result.Failed;
                    }

                    var statusLabel = exportHadError || exporter.PolymeshFailCount > 0
                        ? "Export Complete (partial)" : "Export Complete";
                    var warningLine = exporter.PolymeshFailCount > 0
                        ? $"Warning: {exporter.PolymeshFailCount} of {exporter.PolymeshCount} polymeshes failed.\n\n"
                        : "";

                    TaskDialog.Show(
                        statusLabel,
                        $"{warningLine}" +
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
            finally
            {
                exporter?.CloseLog();
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
