using System;
using System.Reflection;
using Autodesk.Revit.UI;
using System.Windows.Media.Imaging;

namespace DTExtractor
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Create ribbon tab
                string tabName = "DT Engine";
                try
                {
                    application.CreateRibbonTab(tabName);
                }
                catch
                {
                    // Tab may already exist
                }

                // Create ribbon panel
                var panel = application.CreateRibbonPanel(tabName, "Export");

                // Create export button
                var assembly = Assembly.GetExecutingAssembly();
                var assemblyPath = assembly.Location;

                var buttonData = new PushButtonData(
                    "DTExport",
                    "Export to\nDT Engine",
                    assemblyPath,
                    "DTExtractor.Commands.ExportCommand"
                )
                {
                    ToolTip = "Export Revit model to GLB + Parquet for Digital Twin Engine",
                    LongDescription = "Exports geometry as GLB with Draco compression and all parameters as Parquet. " +
                                     "GUID-based data linking enables fast Click-to-Data lookup in the web viewer."
                };

                var button = panel.AddItem(buttonData) as PushButton;

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("DTExtractor Startup Error", ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
