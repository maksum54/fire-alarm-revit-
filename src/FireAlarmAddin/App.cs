using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace FireAlarmAddin
{
    public class App : IExternalApplication
    {
        private const string TabName = "Fire Alarm";

        public Result OnStartup(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(TabName); } catch { /* tab sudah ada */ }
            var panel = app.CreateRibbonPanel(TabName, "NFPA 72 Detector");
            var path = Assembly.GetExecutingAssembly().Location;

            var btn = new PushButtonData("FA_Detector", "Hitung\nDetector", path, typeof(Commands.FireAlarmCommand).FullName)
            {
                ToolTip = "Klik Space, masukkan tinggi, lalu add-in menghitung jumlah smoke/heat detector berdasarkan NFPA 72.",
                LongDescription = "Smoke detector S = 9 m, Heat detector S = 15 m (dengan reduksi ketinggian NFPA 72 Tabel 17.6.3.5.1). " +
                                  "Jarak 1/2 S dari dinding dan S antar detector.",
                LargeImage = UI.Icons.Detector(32),
                Image = UI.Icons.Detector(16)
            };
            panel.AddItem(btn);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
}
