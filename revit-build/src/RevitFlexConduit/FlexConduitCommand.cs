using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitOperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
using RevitInvalidOperationException = Autodesk.Revit.Exceptions.InvalidOperationException;

namespace RevitFlexConduit;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class FlexConduitCommand : IExternalCommand
{
    private const double MinSegmentFeet = 0.01;
    private const double TargetSegmentFeet = 0.75;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        try
        {
            Conduit? template = uidoc.Selection.GetElementIds().Select(doc.GetElement).OfType<Conduit>().FirstOrDefault();
            ElementId typeId = template?.GetTypeId() ?? new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<ConduitType>().FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
            if (typeId == ElementId.InvalidElementId) { TaskDialog.Show("Flex Conduit", "No conduit type is loaded in this project."); return Result.Cancelled; }

            var points = PickPathPoints(uidoc);
            if (points.Count < 2) return Result.Cancelled;
            var path = BuildSmoothPath(points);
            ElementId levelId = template?.ReferenceLevel?.Id ?? ElementId.InvalidElementId;
            double? diameter = template?.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble();

            using var tx = new Transaction(doc, "Create Flex Conduit");
            tx.Start();
            var created = new List<Conduit>();
            for (int i = 0; i < path.Count - 1; i++)
            {
                if (path[i].DistanceTo(path[i + 1]) < MinSegmentFeet) continue;
                var c = Conduit.Create(doc, typeId, path[i], path[i + 1], levelId);
                if (diameter.HasValue)
                {
                    var p = c.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                    if (p != null && !p.IsReadOnly) p.Set(diameter.Value);
                }
                if (template != null) CopyParameter(template, c, "Service Type");
                created.Add(c);
            }
            if (created.Count == 0) { tx.RollBack(); return Result.Cancelled; }
            doc.Regenerate();
            ConnectRun(created);
            TagRun(created);
            tx.Commit();
            uidoc.Selection.SetElementIds(created.Select(x => x.Id).ToList());
            return Result.Succeeded;
        }
        catch (RevitOperationCanceledException) { return Result.Cancelled; }
        catch (Exception ex) { message = ex.ToString(); TaskDialog.Show("Flex Conduit", "Flex conduit could not be created.\n\n" + ex.Message); return Result.Failed; }
    }

    private static List<XYZ> PickPathPoints(UIDocument uidoc)
    {
        var result = new List<XYZ>();
        var snaps = ObjectSnapTypes.Endpoints | ObjectSnapTypes.Midpoints | ObjectSnapTypes.Intersections | ObjectSnapTypes.Nearest | ObjectSnapTypes.Points;
        try
        {
            result.Add(uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick START point"));
            result.Add(uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick next point"));
            while (true)
            {
                try
                {
                    XYZ p = uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick another point, or press ESC to finish");
                    if (result[^1].DistanceTo(p) >= MinSegmentFeet) result.Add(p);
                }
                catch (RevitOperationCanceledException) { break; }
            }
        }
        catch (RevitInvalidOperationException ex)
        {
            TaskDialog.Show("Flex Conduit", "Set a work plane for this view, then run the tool again.\n\n" + ex.Message);
            return new List<XYZ>();
        }
        return result;
    }

    private static List<XYZ> BuildSmoothPath(IReadOnlyList<XYZ> control)
    {
        if (control.Count == 2) return new List<XYZ> { control[0], control[1] };
        var output = new List<XYZ> { control[0] };
        for (int i = 0; i < control.Count - 1; i++)
        {
            XYZ p0 = i == 0 ? control[i] : control[i - 1];
            XYZ p1 = control[i]; XYZ p2 = control[i + 1]; XYZ p3 = i + 2 < control.Count ? control[i + 2] : control[i + 1];
            int divisions = Math.Clamp((int)Math.Ceiling(p1.DistanceTo(p2) / TargetSegmentFeet), 2, 80);
            for (int s = 1; s <= divisions; s++)
            {
                double t = (double)s / divisions; double t2 = t * t; double t3 = t2 * t;
                XYZ pt = new(
                    .5 * ((2*p1.X)+(-p0.X+p2.X)*t+(2*p0.X-5*p1.X+4*p2.X-p3.X)*t2+(-p0.X+3*p1.X-3*p2.X+p3.X)*t3),
                    .5 * ((2*p1.Y)+(-p0.Y+p2.Y)*t+(2*p0.Y-5*p1.Y+4*p2.Y-p3.Y)*t2+(-p0.Y+3*p1.Y-3*p2.Y+p3.Y)*t3),
                    .5 * ((2*p1.Z)+(-p0.Z+p2.Z)*t+(2*p0.Z-5*p1.Z+4*p2.Z-p3.Z)*t2+(-p0.Z+3*p1.Z-3*p2.Z+p3.Z)*t3));
                if (output[^1].DistanceTo(pt) >= MinSegmentFeet) output.Add(pt);
            }
        }
        return output;
    }

    private static void ConnectRun(IReadOnlyList<Conduit> conduits)
    {
        for (int i = 0; i < conduits.Count - 1; i++)
        {
            XYZ junction = ((LocationCurve)conduits[i].Location).Curve.GetEndPoint(1);
            Connector? a = Closest(conduits[i], junction); Connector? b = Closest(conduits[i + 1], junction);
            try { if (a != null && b != null && !a.IsConnectedTo(b)) a.ConnectTo(b); } catch { }
        }
    }

    private static Connector? Closest(Conduit c, XYZ p) => c.ConnectorManager.Connectors.Cast<Connector>().OrderBy(x => x.Origin.DistanceTo(p)).FirstOrDefault();

    private static void CopyParameter(Element source, Element target, string name)
    {
        var a = source.LookupParameter(name); var b = target.LookupParameter(name);
        if (a == null || b == null || b.IsReadOnly || a.StorageType != b.StorageType) return;
        try
        {
            switch (a.StorageType) { case StorageType.String: b.Set(a.AsString() ?? ""); break; case StorageType.Integer: b.Set(a.AsInteger()); break; case StorageType.Double: b.Set(a.AsDouble()); break; case StorageType.ElementId: b.Set(a.AsElementId()); break; }
        } catch { }
    }

    private static void TagRun(IEnumerable<Conduit> conduits)
    {
        string runId = "FLEX-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        foreach (var c in conduits)
        {
            var p = c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (p != null && !p.IsReadOnly && string.IsNullOrWhiteSpace(p.AsString())) p.Set(runId);
        }
    }
}
