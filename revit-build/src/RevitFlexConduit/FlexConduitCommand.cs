using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.ExtensibleStorage;
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
    private const double TargetSegmentFeet = 0.35;
    private const double ControlMarkerHalfSizeFeet = 0.14;
    private static readonly Guid RunSchemaGuid = new("68B6014A-7118-4956-A42D-9D503AB563E8");

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        try
        {
            Conduit? selectedFlex = null;
            string? existingRunId = null;
            List<XYZ>? existingControlPoints = null;

            foreach (Conduit selected in uidoc.Selection.GetElementIds().Select(doc.GetElement).OfType<Conduit>())
            {
                if (TryReadRunData(selected, out string runId, out List<XYZ> points))
                {
                    selectedFlex = selected;
                    existingRunId = runId;
                    existingControlPoints = points;
                    break;
                }
            }

            if (selectedFlex != null && existingRunId != null && existingControlPoints != null)
                return EditExistingRun(uidoc, selectedFlex, existingRunId, existingControlPoints);

            Conduit? template = uidoc.Selection.GetElementIds().Select(doc.GetElement).OfType<Conduit>().FirstOrDefault();
            ConduitSettings settings = CaptureSettings(doc, template, uidoc.ActiveView);
            if (settings.TypeId == ElementId.InvalidElementId)
            {
                TaskDialog.Show("Flex Conduit", "No conduit type is loaded in this project.");
                return Result.Cancelled;
            }

            return CreateInteractiveRun(uidoc, settings);
        }
        catch (RevitOperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.ToString();
            TaskDialog.Show("Flex Conduit", "Flex conduit could not be created.\n\n" + ex.Message);
            return Result.Failed;
        }
    }

    private static Result CreateInteractiveRun(UIDocument uidoc, ConduitSettings settings)
    {
        Document doc = uidoc.Document;
        string runId = "FLEX-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var controlPoints = new List<XYZ>();
        var state = new PreviewState();
        ObjectSnapTypes snaps = DefaultSnaps();

        try
        {
            XYZ start = uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick START control point");
            controlPoints.Add(start);
            RefreshMarkersOnly(doc, state, controlPoints);

            XYZ end = uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick END/control point");
            if (start.DistanceTo(end) < MinSegmentFeet)
            {
                CleanupPreview(doc, state, true);
                return Result.Cancelled;
            }

            controlPoints.Add(end);
            RefreshRouteAndMarkers(doc, uidoc.ActiveView, state, controlPoints, settings, runId);

            while (true)
            {
                try
                {
                    XYZ p = uidoc.Selection.PickPoint(snaps,
                        "Flex Conduit: pick another control point to reshape/extend, or press ESC to finish");
                    if (controlPoints[^1].DistanceTo(p) < MinSegmentFeet) continue;
                    controlPoints.Add(p);
                    RefreshRouteAndMarkers(doc, uidoc.ActiveView, state, controlPoints, settings, runId);
                }
                catch (RevitOperationCanceledException)
                {
                    break;
                }
            }

            DeleteMarkers(doc, state);
            uidoc.Selection.SetElementIds(state.RouteIds);
            return state.RouteIds.Count > 0 ? Result.Succeeded : Result.Cancelled;
        }
        catch (RevitOperationCanceledException)
        {
            CleanupPreview(doc, state, deleteRoute: controlPoints.Count < 2);
            return controlPoints.Count >= 2 && state.RouteIds.Count > 0 ? Result.Succeeded : Result.Cancelled;
        }
        catch (RevitInvalidOperationException ex)
        {
            CleanupPreview(doc, state, true);
            TaskDialog.Show("Flex Conduit", "Set a work plane for this view, then run the tool again.\n\n" + ex.Message);
            return Result.Cancelled;
        }
    }

    private static Result EditExistingRun(UIDocument uidoc, Conduit selectedFlex, string runId, List<XYZ> controlPoints)
    {
        Document doc = uidoc.Document;
        ConduitSettings settings = CaptureSettings(doc, selectedFlex, uidoc.ActiveView);
        var state = new PreviewState
        {
            RouteIds = FindRunElementIds(doc, runId)
        };

        var dialog = new TaskDialog("Flex Conduit")
        {
            MainInstruction = "Edit selected Flex Conduit run",
            MainContent = "The stored control points can be moved individually, or the route can be redrawn with a new set of control points.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.CommandLink1
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Move control points", "Select a visible control point, then pick its new location.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Redraw control points", "Pick a new start, end, and any additional control points.");

        TaskDialogResult choice = dialog.Show();
        if (choice == TaskDialogResult.CommandLink1)
            return MoveExistingControlPoints(uidoc, state, controlPoints, settings, runId);
        if (choice == TaskDialogResult.CommandLink2)
            return RedrawExistingControlPoints(uidoc, state, settings, runId);
        return Result.Cancelled;
    }

    private static Result MoveExistingControlPoints(
        UIDocument uidoc,
        PreviewState state,
        List<XYZ> controlPoints,
        ConduitSettings settings,
        string runId)
    {
        Document doc = uidoc.Document;
        RefreshMarkersOnly(doc, state, controlPoints);
        ObjectSnapTypes snaps = DefaultSnaps();

        try
        {
            while (true)
            {
                Reference picked = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new ElementIdSelectionFilter(() => state.MarkerToControlIndex.Keys),
                    "Flex Conduit: select a control point to move, or press ESC to finish");

                if (!state.MarkerToControlIndex.TryGetValue(picked.ElementId, out int index))
                    continue;

                XYZ newPoint = uidoc.Selection.PickPoint(snaps, $"Flex Conduit: move control point {index + 1} to new location");
                controlPoints[index] = newPoint;
                RefreshRouteAndMarkers(doc, uidoc.ActiveView, state, controlPoints, settings, runId);
            }
        }
        catch (RevitOperationCanceledException)
        {
            DeleteMarkers(doc, state);
            uidoc.Selection.SetElementIds(state.RouteIds);
            return Result.Succeeded;
        }
    }

    private static Result RedrawExistingControlPoints(UIDocument uidoc, PreviewState state, ConduitSettings settings, string runId)
    {
        Document doc = uidoc.Document;
        var controlPoints = new List<XYZ>();
        ObjectSnapTypes snaps = DefaultSnaps();

        try
        {
            controlPoints.Add(uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick NEW START control point"));
            RefreshMarkersOnly(doc, state, controlPoints);

            XYZ second = uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick NEW END/control point");
            if (controlPoints[0].DistanceTo(second) < MinSegmentFeet)
            {
                DeleteMarkers(doc, state);
                return Result.Cancelled;
            }

            controlPoints.Add(second);
            RefreshRouteAndMarkers(doc, uidoc.ActiveView, state, controlPoints, settings, runId);

            while (true)
            {
                try
                {
                    XYZ p = uidoc.Selection.PickPoint(snaps,
                        "Flex Conduit: pick another control point, or press ESC to finish");
                    if (controlPoints[^1].DistanceTo(p) < MinSegmentFeet) continue;
                    controlPoints.Add(p);
                    RefreshRouteAndMarkers(doc, uidoc.ActiveView, state, controlPoints, settings, runId);
                }
                catch (RevitOperationCanceledException)
                {
                    break;
                }
            }

            DeleteMarkers(doc, state);
            uidoc.Selection.SetElementIds(state.RouteIds);
            return Result.Succeeded;
        }
        catch (RevitOperationCanceledException)
        {
            DeleteMarkers(doc, state);
            return Result.Cancelled;
        }
    }

    private static ObjectSnapTypes DefaultSnaps()
        => ObjectSnapTypes.Endpoints |
           ObjectSnapTypes.Midpoints |
           ObjectSnapTypes.Intersections |
           ObjectSnapTypes.Nearest |
           ObjectSnapTypes.Points;

    private static ConduitSettings CaptureSettings(Document doc, Conduit? template, View activeView)
    {
        ElementId typeId = template?.GetTypeId()
            ?? new FilteredElementCollector(doc)
                .OfClass(typeof(ConduitType))
                .Cast<ConduitType>()
                .FirstOrDefault()?.Id
            ?? ElementId.InvalidElementId;

        ElementId levelId = template?.ReferenceLevel?.Id ?? ElementId.InvalidElementId;
        if (levelId == ElementId.InvalidElementId)
        {
            double z = activeView.GenLevel?.Elevation ?? 0.0;
            Level? nearest = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - z))
                .FirstOrDefault();
            levelId = nearest?.Id ?? ElementId.InvalidElementId;
        }

        double? diameter = template?.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble();
        string? serviceType = null;
        Parameter? service = template?.LookupParameter("Service Type");
        if (service?.StorageType == StorageType.String)
            serviceType = service.AsString();

        return new ConduitSettings(typeId, levelId, diameter, serviceType);
    }

    private static void RefreshRouteAndMarkers(
        Document doc,
        View view,
        PreviewState state,
        IReadOnlyList<XYZ> controlPoints,
        ConduitSettings settings,
        string runId)
    {
        using var tx = new Transaction(doc, "Update Flex Conduit Preview");
        tx.Start();

        DeleteElementsNoThrow(doc, state.RouteIds);
        DeleteElementsNoThrow(doc, state.MarkerToControlIndex.Keys);
        state.RouteIds = new List<ElementId>();
        state.MarkerToControlIndex = new Dictionary<ElementId, int>();

        var path = BuildSmoothPath(controlPoints, view);
        var created = new List<Conduit>();

        for (int i = 0; i < path.Count - 1; i++)
        {
            if (path[i].DistanceTo(path[i + 1]) < MinSegmentFeet) continue;

            Conduit conduit = Conduit.Create(doc, settings.TypeId, path[i], path[i + 1], settings.LevelId);
            ApplySettings(conduit, settings);
            WriteRunData(conduit, runId, controlPoints);
            created.Add(conduit);
        }

        doc.Regenerate();
        ConnectRun(created);
        state.RouteIds = created.Select(c => c.Id).ToList();
        CreateControlMarkers(doc, state, controlPoints);

        tx.Commit();
    }

    private static void RefreshMarkersOnly(Document doc, PreviewState state, IReadOnlyList<XYZ> controlPoints)
    {
        using var tx = new Transaction(doc, "Show Flex Conduit Control Points");
        tx.Start();
        DeleteElementsNoThrow(doc, state.MarkerToControlIndex.Keys);
        state.MarkerToControlIndex = new Dictionary<ElementId, int>();
        CreateControlMarkers(doc, state, controlPoints);
        tx.Commit();
    }

    private static void CreateControlMarkers(Document doc, PreviewState state, IReadOnlyList<XYZ> controlPoints)
    {
        ElementId categoryId = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel)?.Id ?? ElementId.InvalidElementId;
        if (categoryId == ElementId.InvalidElementId) return;

        for (int i = 0; i < controlPoints.Count; i++)
        {
            XYZ p = controlPoints[i];
            double s = ControlMarkerHalfSizeFeet;
            var shape = new List<GeometryObject>
            {
                Line.CreateBound(p - XYZ.BasisX.Multiply(s), p + XYZ.BasisX.Multiply(s)),
                Line.CreateBound(p - XYZ.BasisY.Multiply(s), p + XYZ.BasisY.Multiply(s)),
                Line.CreateBound(p - XYZ.BasisZ.Multiply(s), p + XYZ.BasisZ.Multiply(s))
            };

            DirectShape marker = DirectShape.CreateElement(doc, categoryId);
            marker.Name = $"Flex Conduit Control Point {i + 1}";
            marker.SetShape(shape);
            state.MarkerToControlIndex[marker.Id] = i;
        }
    }

    private static List<XYZ> BuildSmoothPath(IReadOnlyList<XYZ> control, View view)
    {
        if (control.Count < 2) return control.ToList();

        if (control.Count == 2)
        {
            XYZ start = control[0];
            XYZ end = control[1];
            XYZ delta = end - start;
            double distance = delta.GetLength();
            if (distance < MinSegmentFeet) return new List<XYZ> { start, end };

            XYZ direction = delta.Normalize();
            XYZ guide = view.UpDirection;
            XYZ perpendicular = guide - direction.Multiply(guide.DotProduct(direction));
            if (perpendicular.GetLength() < 1e-6)
            {
                guide = view.RightDirection;
                perpendicular = guide - direction.Multiply(guide.DotProduct(direction));
            }
            if (perpendicular.GetLength() < 1e-6)
                perpendicular = XYZ.BasisZ.CrossProduct(direction);
            if (perpendicular.GetLength() < 1e-6)
                perpendicular = XYZ.BasisY;
            perpendicular = perpendicular.Normalize();

            double bow = Math.Min(distance * 0.06, 0.50);
            XYZ c1 = start + delta.Multiply(0.33) + perpendicular.Multiply(bow);
            XYZ c2 = start + delta.Multiply(0.66) + perpendicular.Multiply(bow);
            int divisions = Math.Clamp((int)Math.Ceiling(distance / TargetSegmentFeet), 4, 120);
            var result = new List<XYZ>(divisions + 1) { start };
            for (int i = 1; i <= divisions; i++)
            {
                double t = (double)i / divisions;
                double u = 1.0 - t;
                XYZ point = start.Multiply(u * u * u)
                    + c1.Multiply(3 * u * u * t)
                    + c2.Multiply(3 * u * t * t)
                    + end.Multiply(t * t * t);
                if (result[^1].DistanceTo(point) >= MinSegmentFeet)
                    result.Add(point);
            }
            return result;
        }

        var output = new List<XYZ> { control[0] };
        for (int i = 0; i < control.Count - 1; i++)
        {
            XYZ p0 = i == 0 ? control[i] : control[i - 1];
            XYZ p1 = control[i];
            XYZ p2 = control[i + 1];
            XYZ p3 = i + 2 < control.Count ? control[i + 2] : control[i + 1];
            int divisions = Math.Clamp((int)Math.Ceiling(p1.DistanceTo(p2) / TargetSegmentFeet), 3, 120);

            for (int s = 1; s <= divisions; s++)
            {
                double t = (double)s / divisions;
                double t2 = t * t;
                double t3 = t2 * t;
                XYZ pt = new(
                    .5 * ((2 * p1.X) + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                    .5 * ((2 * p1.Y) + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3),
                    .5 * ((2 * p1.Z) + (-p0.Z + p2.Z) * t + (2 * p0.Z - 5 * p1.Z + 4 * p2.Z - p3.Z) * t2 + (-p0.Z + 3 * p1.Z - 3 * p2.Z + p3.Z) * t3));

                if (output[^1].DistanceTo(pt) >= MinSegmentFeet)
                    output.Add(pt);
            }
        }
        return output;
    }

    private static void ApplySettings(Conduit conduit, ConduitSettings settings)
    {
        if (settings.Diameter.HasValue)
        {
            Parameter? diameter = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
            if (diameter != null && !diameter.IsReadOnly)
                diameter.Set(settings.Diameter.Value);
        }

        if (!string.IsNullOrWhiteSpace(settings.ServiceType))
        {
            Parameter? service = conduit.LookupParameter("Service Type");
            if (service?.StorageType == StorageType.String && !service.IsReadOnly)
                service.Set(settings.ServiceType);
        }
    }

    private static void ConnectRun(IReadOnlyList<Conduit> conduits)
    {
        for (int i = 0; i < conduits.Count - 1; i++)
        {
            XYZ junction = ((LocationCurve)conduits[i].Location).Curve.GetEndPoint(1);
            Connector? a = Closest(conduits[i], junction);
            Connector? b = Closest(conduits[i + 1], junction);
            try
            {
                if (a != null && b != null && !a.IsConnectedTo(b))
                    a.ConnectTo(b);
            }
            catch
            {
            }
        }
    }

    private static Connector? Closest(Conduit conduit, XYZ point)
        => conduit.ConnectorManager.Connectors.Cast<Connector>()
            .OrderBy(c => c.Origin.DistanceTo(point))
            .FirstOrDefault();

    private static Schema GetOrCreateRunSchema()
    {
        Schema? schema = Schema.Lookup(RunSchemaGuid);
        if (schema != null) return schema;

        var builder = new SchemaBuilder(RunSchemaGuid);
        builder.SetSchemaName("RevitFlexConduitRun");
        builder.SetVendorId("CTRN");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("RunId", typeof(string));
        builder.AddSimpleField("ControlPoints", typeof(string));
        return builder.Finish();
    }

    private static void WriteRunData(Element element, string runId, IReadOnlyList<XYZ> controlPoints)
    {
        Schema schema = GetOrCreateRunSchema();
        var entity = new Entity(schema);
        entity.Set(schema.GetField("RunId"), runId);
        entity.Set(schema.GetField("ControlPoints"), SerializePoints(controlPoints));
        element.SetEntity(entity);
    }

    private static bool TryReadRunData(Element element, out string runId, out List<XYZ> controlPoints)
    {
        runId = string.Empty;
        controlPoints = new List<XYZ>();
        Schema? schema = Schema.Lookup(RunSchemaGuid);
        if (schema == null) return false;

        Entity entity = element.GetEntity(schema);
        if (!entity.IsValid()) return false;

        try
        {
            runId = entity.Get<string>(schema.GetField("RunId")) ?? string.Empty;
            string serialized = entity.Get<string>(schema.GetField("ControlPoints")) ?? string.Empty;
            controlPoints = DeserializePoints(serialized);
            return !string.IsNullOrWhiteSpace(runId) && controlPoints.Count >= 2;
        }
        catch
        {
            return false;
        }
    }

    private static List<ElementId> FindRunElementIds(Document doc, string runId)
    {
        return new FilteredElementCollector(doc).OfClass(typeof(Conduit)).Cast<Conduit>()
            .Where(c => TryReadRunData(c, out string candidate, out _) && candidate == runId)
            .Select(c => c.Id).ToList();
    }

    private static string SerializePoints(IReadOnlyList<XYZ> points)
        => string.Join(";", points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:R},{p.Y:R},{p.Z:R}")));

    private static List<XYZ> DeserializePoints(string value)
    {
        var points = new List<XYZ>();
        foreach (string token in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] xyz = token.Split(',');
            if (xyz.Length != 3) continue;
            if (double.TryParse(xyz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(xyz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                double.TryParse(xyz[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                points.Add(new XYZ(x, y, z));
        }
        return points;
    }

    private static void DeleteMarkers(Document doc, PreviewState state)
    {
        if (state.MarkerToControlIndex.Count == 0) return;
        using var tx = new Transaction(doc, "Hide Flex Conduit Control Points");
        tx.Start();
        DeleteElementsNoThrow(doc, state.MarkerToControlIndex.Keys);
        state.MarkerToControlIndex = new Dictionary<ElementId, int>();
        tx.Commit();
    }

    private static void CleanupPreview(Document doc, PreviewState state, bool deleteRoute)
    {
        using var tx = new Transaction(doc, "Cancel Flex Conduit Preview");
        tx.Start();
        DeleteElementsNoThrow(doc, state.MarkerToControlIndex.Keys);
        state.MarkerToControlIndex = new Dictionary<ElementId, int>();
        if (deleteRoute)
        {
            DeleteElementsNoThrow(doc, state.RouteIds);
            state.RouteIds = new List<ElementId>();
        }
        tx.Commit();
    }

    private static void DeleteElementsNoThrow(Document doc, IEnumerable<ElementId> ids)
    {
        var valid = ids.Where(id => id != ElementId.InvalidElementId && doc.GetElement(id) != null).Distinct().ToList();
        if (valid.Count == 0) return;
        try { doc.Delete(valid); } catch { }
    }

    private sealed class ElementIdSelectionFilter : ISelectionFilter
    {
        private readonly Func<IEnumerable<ElementId>> _ids;
        public ElementIdSelectionFilter(Func<IEnumerable<ElementId>> ids) => _ids = ids;
        public bool AllowElement(Element elem) => _ids().Contains(elem.Id);
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    private sealed class PreviewState
    {
        public List<ElementId> RouteIds { get; set; } = new();
        public Dictionary<ElementId, int> MarkerToControlIndex { get; set; } = new();
    }

    private sealed record ConduitSettings(ElementId TypeId, ElementId LevelId, double? Diameter, string? ServiceType);
}
