using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace FireAlarmAddin.Core
{
    /// <summary>Membaca boundary Space dan mengubahnya ke sistem lokal (meter) searah dinding terpanjang.</summary>
    public class SpaceGeometry
    {
        public string Name, Number, LevelName;
        public ElementId LevelId;
        public double LevelElevationFt;
        public double Length, Width, Area;       // meter / m2
        public double DefaultHeight;             // meter (unbounded height)
        public List<List<Pt>> LocalLoops = new List<List<Pt>>();

        private double _angle, _minX, _minY;     // transformasi lokal -> global

        public static double ToM(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
        public static double ToFt(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

        public static SpaceGeometry From(SpatialElement space)
        {
            var g = new SpaceGeometry
            {
                Name = space.Name,
                Number = space.Number,
                Area = UnitUtils.ConvertFromInternalUnits(space.Area, UnitTypeId.SquareMeters)
            };
            var level = space.Document.GetElement(space.LevelId) as Level;
            g.LevelId = space.LevelId;
            g.LevelName = level?.Name ?? "-";
            g.LevelElevationFt = level?.Elevation ?? 0;
            var hp = space.get_Parameter(BuiltInParameter.ROOM_HEIGHT);
            g.DefaultHeight = hp != null && hp.HasValue ? Math.Round(ToM(hp.AsDouble()), 2) : 3.0;

            var opt = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            var loops = new List<List<XYZ>>();
            double longest = 0; XYZ dir = XYZ.BasisX;
            foreach (var segs in space.GetBoundarySegments(opt) ?? new List<IList<BoundarySegment>>())
            {
                var pts = new List<XYZ>();
                foreach (var s in segs)
                {
                    var c = s.GetCurve();
                    if (c is Line && c.Length > longest)
                    {
                        longest = c.Length;
                        dir = (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
                    }
                    var tess = c.Tessellate();
                    for (int i = 0; i < tess.Count - 1; i++) pts.Add(tess[i]);
                }
                if (pts.Count > 2) loops.Add(pts);
            }
            if (loops.Count == 0) throw new InvalidOperationException(
                "Space \"" + g.Name + "\" tidak memiliki boundary (Not Enclosed / Not Placed).");

            g._angle = Math.Atan2(dir.Y, dir.X);
            double cos = Math.Cos(-g._angle), sin = Math.Sin(-g._angle);
            var rotated = loops.Select(l => l.Select(p => new Pt(
                ToM(p.X * cos - p.Y * sin), ToM(p.X * sin + p.Y * cos))).ToList()).ToList();

            var all = rotated.SelectMany(l => l).ToList();
            g._minX = all.Min(p => p.X); g._minY = all.Min(p => p.Y);
            g.Length = all.Max(p => p.X) - g._minX;
            g.Width = all.Max(p => p.Y) - g._minY;
            if (g.Width > g.Length)
            {   // pastikan panjang >= lebar (tukar sumbu dengan rotasi 90°)
                g._angle += Math.PI / 2;
                return RebuildRotated(g, loops);
            }
            g.LocalLoops = rotated.Select(l => l.Select(p => new Pt(p.X - g._minX, p.Y - g._minY)).ToList()).ToList();
            return g;
        }

        private static SpaceGeometry RebuildRotated(SpaceGeometry g, List<List<XYZ>> loops)
        {
            double cos = Math.Cos(-g._angle), sin = Math.Sin(-g._angle);
            var rotated = loops.Select(l => l.Select(p => new Pt(
                ToM(p.X * cos - p.Y * sin), ToM(p.X * sin + p.Y * cos))).ToList()).ToList();
            var all = rotated.SelectMany(l => l).ToList();
            g._minX = all.Min(p => p.X); g._minY = all.Min(p => p.Y);
            g.Length = all.Max(p => p.X) - g._minX;
            g.Width = all.Max(p => p.Y) - g._minY;
            g.LocalLoops = rotated.Select(l => l.Select(p => new Pt(p.X - g._minX, p.Y - g._minY)).ToList()).ToList();
            return g;
        }

        /// <summary>Titik lokal (m) -> XYZ global (feet) pada ketinggian tertentu dari level.</summary>
        public XYZ ToWorld(Pt p, double heightM)
        {
            double x = ToFt(p.X + _minX), y = ToFt(p.Y + _minY);
            double cos = Math.Cos(_angle), sin = Math.Sin(_angle);
            return new XYZ(x * cos - y * sin, x * sin + y * cos, LevelElevationFt + ToFt(heightM));
        }
    }
}
