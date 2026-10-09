using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FireAlarmAddin.Core;
using FireAlarmAddin.UI;

namespace FireAlarmAddin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class FireAlarmCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc.Document;
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_FireAlarmDevices).Cast<FamilySymbol>()
                .OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();

            FireAlarmWindow.Settings settings = null;
            while (true)
            {
                Space space;
                try
                {
                    var r = uidoc.Selection.PickObject(ObjectType.Element, new SpaceFilter(),
                        "Klik sebuah Space (MEP Space) untuk menghitung fire alarm detector. ESC untuk batal.");
                    space = doc.GetElement(r) as Space;
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

                SpaceGeometry geo;
                try { geo = SpaceGeometry.From(space); }
                catch (Exception ex) { TaskDialog.Show("Fire Alarm", ex.Message); continue; }

                var win = new FireAlarmWindow(geo, symbols, settings);
                new System.Windows.Interop.WindowInteropHelper(win).Owner = data.Application.MainWindowHandle;
                win.ShowDialog();
                settings = win.CurrentSettings;

                if (win.Action == FireAlarmWindow.UserAction.Place)
                {
                    int n = Place(doc, space, geo, win.Result, win.CurrentSettings, out int skipped, out int replaced, out string err);
                    TaskDialog.Show("Fire Alarm", err ?? n + " detector berhasil ditempatkan di space \"" + geo.Name + "\"." +
                        (replaced > 0 ? "\n" + replaced + " detector lama di space ini dihapus dan diganti." : "") +
                        (skipped > 0 ? "\n" + skipped + " titik di luar boundary space dilewati/dihapus." : ""));
                }
                if (win.Action == FireAlarmWindow.UserAction.Close) return Result.Succeeded;
                // Place / PickAnother -> ulangi pilih space
            }
        }

        private static int Place(Document doc, Space space, SpaceGeometry geo, CalcResult res, FireAlarmWindow.Settings s,
            out int skipped, out int replaced, out string error)
        {
            error = null; skipped = 0; replaced = 0;
            var symbol = s.Symbol;
            if (symbol == null) { error = "Tidak ada family Fire Alarm Device yang dipilih / dimuat di project."; return 0; }
            var level = doc.GetElement(geo.LevelId) as Level;
            int count = 0;
            using (var t = new Transaction(doc, "Place Fire Alarm Detectors"))
            {
                t.Start();
                try
                {
                    replaced = RemoveExisting(doc, space, geo, s);
                    if (!symbol.IsActive) symbol.Activate();
                    var placement = symbol.Family.FamilyPlacementType;
                    ReferencePlane plane = null;
                    if (placement == FamilyPlacementType.WorkPlaneBased)
                    {
                        // bidang horizontal menghadap ke bawah pada ketinggian detector
                        var z = geo.ToWorld(new Pt(0, 0), s.Height).Z;
                        plane = doc.Create.NewReferencePlane2(new XYZ(0, 0, z), new XYZ(1, 0, z), new XYZ(0, 1, z), doc.ActiveView);
                        plane.Name = "FA " + geo.Number + " " + s.Height.ToString("0.00") + "m " + Guid.NewGuid().ToString("N").Substring(0, 4);
                    }
                    foreach (var p in res.Points)
                    {
                        var xyz = geo.ToWorld(p, s.Height);
                        // cek ulang dengan geometri Revit (di dekat lantai, karena tinggi space bisa < tinggi detector)
                        var test = new XYZ(xyz.X, xyz.Y, geo.LevelElevationFt + SpaceGeometry.ToFt(0.1));
                        if (!space.IsPointInSpace(test)) { skipped++; continue; }
                        FamilyInstance fi;
                        if (plane != null)
                            fi = doc.Create.NewFamilyInstance(plane.GetReference(), xyz, XYZ.BasisX, symbol);
                        else
                        {
                            fi = doc.Create.NewFamilyInstance(xyz, symbol, level, StructuralType.NonStructural);
                            var elev = fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                            if (elev != null && !elev.IsReadOnly) elev.Set(SpaceGeometry.ToFt(s.Height));
                        }
                        fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(
                            (s.Type == DetectorType.Smoke ? "Smoke" : "Heat") + " - " + geo.Number + " " + geo.Name);
                        count++;
                    }
                    t.Commit();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    error = "Gagal menempatkan detector: " + ex.Message +
                            "\nGunakan family non-hosted atau face/work-plane based dari kategori Fire Alarm Devices.";
                    return 0;
                }
            }
            return count;
        }

        /// <summary>
        /// Hapus detector yang dulu ditempatkan add-in ini di space yang sama (Comments "Smoke - ..." / "Heat - ..."),
        /// supaya menempatkan ulang tidak menumpuk. Fire alarm device lain (bell, MCP, panel) tidak disentuh.
        /// </summary>
        private static int RemoveExisting(Document doc, Space space, SpaceGeometry geo, FireAlarmWindow.Settings s)
        {
            double zTest = geo.LevelElevationFt + SpaceGeometry.ToFt(0.1);
            double zMin = geo.LevelElevationFt - SpaceGeometry.ToFt(0.1);
            double zMax = geo.LevelElevationFt + SpaceGeometry.ToFt(Math.Max(geo.DefaultHeight, s.Height) + 1.0);
            var old = new List<ElementId>();
            var planes = new HashSet<ElementId>();
            foreach (var fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_FireAlarmDevices).Cast<FamilyInstance>())
            {
                var mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                if (!mark.StartsWith("Smoke - ") && !mark.StartsWith("Heat - ")) continue;
                var pt = (fi.Location as LocationPoint)?.Point;
                if (pt == null || pt.Z < zMin || pt.Z > zMax) continue; // lantai lain di posisi denah yang sama
                if (!space.IsPointInSpace(new XYZ(pt.X, pt.Y, zTest))) continue;
                old.Add(fi.Id);
                if (fi.Host is ReferencePlane rp && rp.Name.StartsWith("FA ")) planes.Add(rp.Id);
            }
            if (old.Count == 0) return 0;
            doc.Delete(old);
            // reference plane buatan add-in yang sudah tidak dipakai detector lain
            foreach (var id in planes)
            {
                var rp = doc.GetElement(id);
                if (rp != null && rp.GetDependentElements(new ElementClassFilter(typeof(FamilyInstance))).Count == 0)
                    doc.Delete(id);
            }
            return old.Count;
        }

        private class SpaceFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Space;
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }
}
