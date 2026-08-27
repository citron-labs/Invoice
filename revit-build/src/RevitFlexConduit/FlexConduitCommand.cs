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
    private const double MinPointSpacingFeet = 0.01;
    private const double ControlMarkerHalfSizeFeet = 0.16;
    private const double DefaultDiameterFeet = 1.0 / 12.0;

    private static readonly Guid RunSchemaGuid = new("68B6014A-7118-4956-A42D-9D503AB563E8");
    private static readonly Guid MarkerSchemaGuid = new("7D6EF0AD-CF43-4C58-A9A8-48B0FBF4272A");
    private static readonly Guid SettingsSchemaGuid = new("D2BD54C8-111D-4697-8AE1-B2D353546E25");

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        try
        {
            List<Element> selected = uidoc.Selection.GetElementIds()
                .Select(doc.GetElement)
                .Where(e => e != null)
                .Cast<Element>()
                .ToList();

            Element? selectedMarker = selected.FirstOrDefault(e => TryReadMarkerData(e, out _, out _));
            if (selectedMarker != null &&
                TryReadMarkerData(selectedMarker, out string markerRunId, out int markerIndex) &&
                TryReadRunData(selectedMarker, out _, out List<XYZ> markerPoints))
            {
                ConduitSettings markerSettings = TryReadSettings(selectedMarker, out ConduitSettings storedMarkerSettings)
                    ? storedMarkerSettings
                    : CaptureSettings(doc, selected.OfType<Conduit>().FirstOrDefault(), uidoc.ActiveView);
                return MoveSinglePersistentControlPoint(uidoc, markerRunId, markerIndex, markerPoints, markerSettings);
            }

            Element? selectedFlex = selected.FirstOrDefault(e =>
                TryReadRunData(e, out _, out _) && !TryReadMarkerData(e, out _, out _));

            if (selectedFlex != null &&
                TryReadRunData(selectedFlex, out string existingRunId, out List<XYZ> existingControlPoints))
            {
                ConduitSettings existingSettings = TryReadSettings(selectedFlex, out ConduitSettings storedSettings)
                    ? storedSettings
                    : CaptureSettings(doc, selected.OfType<Conduit>().FirstOrDefault(), uidoc.ActiveView);
                return EditExistingRun(uidoc, selectedFlex, existingRunId, existingControlPoints, existingSettings);
            }

            Conduit? template = selected.OfType<Conduit>().FirstOrDefault();
            ConduitSettings settings = CaptureSettings(doc, template, uidoc.ActiveView);
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
        var state = new FlexState();
        ObjectSnapTypes snaps = DefaultSnaps();

        try
        {
            XYZ start = uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick START point");
            controlPoints.Add(start);
            SyncPersistentMarkers(doc, state, controlPoints, runId, settings);

            XYZ end = uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick END point");
            if (start.DistanceTo(end) < MinPointSpacingFeet)
            {
                CleanupCancelledNewRun(doc, state);
                return Result.Cancelled;
            }

            controlPoints.Add(CreateAutomaticMiddleControlPoint(start, end, uidoc.ActiveView));
            controlPoints.Add(end);
            RefreshSplineAndMarkers(doc, state, controlPoints, settings, runId);

            while (true)
            {
                try
                {
                    XYZ extra = uidoc.Selection.PickPoint(
                        snaps,
                        "Flex Conduit: pick another control point (inserted before the end), or press ESC to finish");

                    if (controlPoints.Any(p => p.DistanceTo(extra) < MinPointSpacingFeet))
                        continue;

                    controlPoints.Insert(controlPoints.Count - 1, extra);
                    RefreshSplineAndMarkers(doc, state, controlPoints, settings, runId);
                }
                catch (RevitOperationCanceledException)
                {
                    break;
                }
            }

            uidoc.Selection.SetElementIds(state.SplineIds);
            return state.SplineIds.Count > 0 ? Result.Succeeded : Result.Cancelled;
        }
        catch (RevitOperationCanceledException)
        {
            if (state.SplineIds.Count == 0)
                CleanupCancelledNewRun(doc, state);
            return state.SplineIds.Count > 0 ? Result.Succeeded : Result.Cancelled;
        }
        catch (RevitInvalidOperationException ex)
        {
            CleanupCancelledNewRun(doc, state);
            TaskDialog.Show("Flex Conduit", "Set a work plane for this view, then run the tool again.\n\n" + ex.Message);
            return Result.Cancelled;
        }
    }

    private static Result EditExistingRun(
        UIDocument uidoc,
        Element selectedFlex,
        string runId,
        List<XYZ> controlPoints,
        ConduitSettings settings)
    {
        Document doc = uidoc.Document;
        var state = LoadState(doc, runId);
        EnsurePersistentMarkers(doc, state, controlPoints, runId, settings);

        var dialog = new TaskDialog("Flex Conduit")
        {
            MainInstruction = "Edit Flex Conduit spline",
            MainContent = "Control points remain in the model. Move any point to reshape the same spline, or redraw the control-point set.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.CommandLink1
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Move control points", "Select a persistent control marker, then pick its new location.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Redraw control points", "Replace the current point set with a new spline path.");

        TaskDialogResult choice = dialog.Show();
        if (choice == TaskDialogResult.CommandLink1)
            return MoveExistingControlPoints(uidoc, state, controlPoints, settings, runId);
        if (choice == TaskDialogResult.CommandLink2)
            return RedrawExistingControlPoints(uidoc, state, controlPoints, settings, runId);
        return Result.Cancelled;
    }

    private static Result MoveSinglePersistentControlPoint(
        UIDocument uidoc,
        string runId,
        int index,
        List<XYZ> controlPoints,
        ConduitSettings settings)
    {
        if (index < 0 || index >= controlPoints.Count)
            return Result.Cancelled;

        Document doc = uidoc.Document;
        var state = LoadState(doc, runId);
        EnsurePersistentMarkers(doc, state, controlPoints, runId, settings);

        try
        {
            XYZ newPoint = uidoc.Selection.PickPoint(DefaultSnaps(), $"Flex Conduit: move control point {index + 1}");
            controlPoints[index] = newPoint;
            RefreshSplineAndMarkers(doc, state, controlPoints, settings, runId);
            uidoc.Selection.SetElementIds(state.MarkerToControlIndex.Where(kvp => kvp.Value == index).Select(kvp => kvp.Key).ToList());
            return Result.Succeeded;
        }
        catch (RevitOperationCanceledException)
        {
            return Result.Cancelled;
        }
    }

    private static Result MoveExistingControlPoints(
        UIDocument uidoc,
        FlexState state,
        List<XYZ> controlPoints,
        ConduitSettings settings,
        string runId)
    {
        Document doc = uidoc.Document;
        EnsurePersistentMarkers(doc, state, controlPoints, runId, settings);

        try
        {
            while (true)
            {
                Reference picked = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new ElementIdSelectionFilter(() => state.MarkerToControlIndex.Keys),
                    "Flex Conduit: select a persistent control point to move, or press ESC to finish");

                if (!state.MarkerToControlIndex.TryGetValue(picked.ElementId, out int index))
                    continue;

                XYZ newPoint = uidoc.Selection.PickPoint(DefaultSnaps(), $"Flex Conduit: move control point {index + 1}");
                controlPoints[index] = newPoint;
                RefreshSplineAndMarkers(doc, state, controlPoints, settings, runId);
            }
        }
        catch (RevitOperationCanceledException)
        {
            uidoc.Selection.SetElementIds(state.SplineIds);
            return Result.Succeeded;
        }
    }

    private static Result RedrawExistingControlPoints(
        UIDocument uidoc,
        FlexState state,
        List<XYZ> originalControlPoints,
        ConduitSettings settings,
        string runId)
    {
        var newPoints = new List<XYZ>();
        ObjectSnapTypes snaps = DefaultSnaps();

        try
        {
            XYZ start = uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick NEW START point");
            XYZ end = uidoc.Selection.PickPoint(snaps, "Flex Conduit: pick NEW END point");
            if (start.DistanceTo(end) < MinPointSpacingFeet)
                return Result.Cancelled;

            newPoints.Add(start);
            newPoints.Add(CreateAutomaticMiddleControlPoint(start, end, uidoc.ActiveView));
            newPoints.Add(end);
            RefreshSplineAndMarkers(uidoc.Document, state, newPoints, settings, runId);

            while (true)
            {
                try
                {
                    XYZ extra = uidoc.Selection.PickPoint(snaps, "Flex Conduit: add another control point, or press ESC to finish");
                    if (newPoints.Any(p => p.DistanceTo(extra) < MinPointSpacingFeet))
                        continue;
                    newPoints.Insert(newPoints.Count - 1, extra);
                    RefreshSplineAndMarkers(uidoc.Document, state, newPoints, settings, runId);
                }
                catch (RevitOperationCanceledException)
                {
                    break;
                }
            }

            uidoc.Selection.SetElementIds(state.SplineIds);
            return Result.Succeeded;
        }
        catch (RevitOperationCanceledException)
        {
            if (newPoints.Count == 0)
                EnsurePersistentMarkers(uidoc.Document, state, originalControlPoints, runId, settings);
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

        double diameter = template?.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble() ?? DefaultDiameterFeet;
        if (diameter <= 0) diameter = DefaultDiameterFeet;

        string serviceType = string.Empty;
        Parameter? service = template?.LookupParameter("Service Type");
        if (service?.StorageType == StorageType.String)
            serviceType = service.AsString() ?? string.Empty;

        return new ConduitSettings(typeId, levelId, diameter, serviceType);
    }

    private static XYZ CreateAutomaticMiddleControlPoint(XYZ start, XYZ end, View view)
    {
        XYZ delta = end - start;
        double distance = delta.GetLength();
        XYZ midpoint = (start + end).Multiply(0.5);
        if (distance < MinPointSpacingFeet)
            return midpoint;

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
        double bow = Math.Clamp(distance * 0.08, 0.15, 0.75);
        return midpoint + perpendicular.Multiply(bow);
    }

    private static void RefreshSplineAndMarkers(
        Document doc,
        FlexState state,
        IReadOnlyList<XYZ> controlPoints,
        ConduitSettings settings,
        string runId)
    {
        using var tx = new Transaction(doc, "Update Flex Conduit Spline");
        tx.Start();

        DeleteElementsNoThrow(doc, state.SplineIds);
        state.SplineIds = new List<ElementId>();

        HermiteSpline spline = HermiteSpline.Create(controlPoints.ToList(), false);
        DirectShape body = CreateSplineBody(doc, spline, settings.Diameter);
        body.Name = $"Flex Conduit Spline {runId}";
        WriteRunData(body, runId, controlPoints);
        WriteSettings(body, settings);
        state.SplineIds.Add(body.Id);

        SyncPersistentMarkersInTransaction(doc, state, controlPoints, runId, settings);
        tx.Commit();
    }

    private static DirectShape CreateSplineBody(Document doc, HermiteSpline spline, double diameter)
    {
        ElementId conduitCategoryId = Category.GetCategory(doc, BuiltInCategory.OST_Conduit)?.Id ?? ElementId.InvalidElementId;
        ElementId genericCategoryId = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel)?.Id ?? ElementId.InvalidElementId;
        ElementId categoryId = conduitCategoryId != ElementId.InvalidElementId && DirectShape.IsValidCategoryId(conduitCategoryId, doc)
            ? conduitCategoryId
            : genericCategoryId;

        DirectShape body = DirectShape.CreateElement(doc, categoryId);
        var geometry = new List<GeometryObject>();

        try
        {
            Solid swept = CreateSplineTube(spline, Math.Max(diameter * 0.5, 0.01));
            geometry.Add(swept);
        }
        catch
        {
            geometry.Add(spline);
        }

        body.SetShape(geometry);
        return body;
    }

    private static Solid CreateSplineTube(Curve spline, double radius)
    {
        double startParameter = spline.GetEndParameter(0);
        Transform derivatives = spline.ComputeDerivatives(startParameter, false);
        XYZ tangent = derivatives.BasisX.Normalize();

        XYZ axisX = XYZ.BasisZ.CrossProduct(tangent);
        if (axisX.GetLength() < 1e-6)
            axisX = XYZ.BasisX.CrossProduct(tangent);
        if (axisX.GetLength() < 1e-6)
            axisX = XYZ.BasisY;
        axisX = axisX.Normalize();
        XYZ axisY = tangent.CrossProduct(axisX).Normalize();
        XYZ center = spline.GetEndPoint(0);

        Arc arc1 = Arc.Create(center, radius, 0.0, Math.PI, axisX, axisY);
        Arc arc2 = Arc.Create(center, radius, Math.PI, 2.0 * Math.PI, axisX, axisY);
        var profile = new CurveLoop();
        profile.Append(arc1);
        profile.Append(arc2);

        var path = new CurveLoop();
        path.Append(spline);
        return GeometryCreationUtilities.CreateSweptGeometry(path, 0, startParameter, new List<CurveLoop> { profile });
    }

    private static void SyncPersistentMarkers(
        Document doc,
        FlexState state,
        IReadOnlyList<XYZ> controlPoints,
        string runId,
        ConduitSettings settings)
    {
        using var tx = new Transaction(doc, "Update Flex Conduit Control Points");
        tx.Start();
        SyncPersistentMarkersInTransaction(doc, state, controlPoints, runId, settings);
        tx.Commit();
    }

    private static void EnsurePersistentMarkers(
        Document doc,
        FlexState state,
        IReadOnlyList<XYZ> controlPoints,
        string runId,
        ConduitSettings settings)
    {
        if (state.MarkerToControlIndex.Count == controlPoints.Count)
            return;
        SyncPersistentMarkers(doc, state, controlPoints, runId, settings);
    }

    private static void SyncPersistentMarkersInTransaction(
        Document doc,
        FlexState state,
        IReadOnlyList<XYZ> controlPoints,
        string runId,
        ConduitSettings settings)
    {
        Dictionary<int, ElementId> existingByIndex = state.MarkerToControlIndex
            .ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

        foreach ((int index, ElementId id) in existingByIndex.ToList())
        {
            if (index >= controlPoints.Count || doc.GetElement(id) == null)
            {
                DeleteElementsNoThrow(doc, new[] { id });
                existingByIndex.Remove(index);
            }
        }

        ElementId genericCategoryId = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel)?.Id ?? ElementId.InvalidElementId;
        var newMap = new Dictionary<ElementId, int>();

        for (int i = 0; i < controlPoints.Count; i++)
        {
            DirectShape marker;
            if (existingByIndex.TryGetValue(i, out ElementId existingId) && doc.GetElement(existingId) is DirectShape existingMarker)
            {
                marker = existingMarker;
            }
            else
            {
                marker = DirectShape.CreateElement(doc, genericCategoryId);
            }

            marker.Name = $"Flex Conduit Control Point {i + 1} [{runId}]";
            marker.SetShape(CreateMarkerGeometry(controlPoints[i]));
            WriteRunData(marker, runId, controlPoints);
            WriteMarkerData(marker, runId, i);
            WriteSettings(marker, settings);
            newMap[marker.Id] = i;
        }

        state.MarkerToControlIndex = newMap;
    }

    private static List<GeometryObject> CreateMarkerGeometry(XYZ point)
    {
        double s = ControlMarkerHalfSizeFeet;
        return new List<GeometryObject>
        {
            Line.CreateBound(point - XYZ.BasisX.Multiply(s), point + XYZ.BasisX.Multiply(s)),
            Line.CreateBound(point - XYZ.BasisY.Multiply(s), point + XYZ.BasisY.Multiply(s)),
            Line.CreateBound(point - XYZ.BasisZ.Multiply(s), point + XYZ.BasisZ.Multiply(s))
        };
    }

    private static FlexState LoadState(Document doc, string runId)
    {
        return new FlexState
        {
            SplineIds = FindSplineElementIds(doc, runId),
            MarkerToControlIndex = FindControlMarkerMap(doc, runId)
        };
    }

    private static List<ElementId> FindSplineElementIds(Document doc, string runId)
    {
        var ids = new List<ElementId>();

        foreach (DirectShape shape in new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>())
        {
            if (TryReadMarkerData(shape, out _, out _)) continue;
            if (TryReadRunData(shape, out string candidate, out _) && candidate == runId)
                ids.Add(shape.Id);
        }

        foreach (Conduit conduit in new FilteredElementCollector(doc).OfClass(typeof(Conduit)).Cast<Conduit>())
        {
            if (TryReadRunData(conduit, out string candidate, out _) && candidate == runId)
                ids.Add(conduit.Id);
        }

        return ids;
    }

    private static Dictionary<ElementId, int> FindControlMarkerMap(Document doc, string runId)
    {
        var map = new Dictionary<ElementId, int>();
        foreach (DirectShape shape in new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>())
        {
            if (TryReadMarkerData(shape, out string candidateRunId, out int index) && candidateRunId == runId)
                map[shape.Id] = index;
        }
        return map;
    }

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

    private static Schema GetOrCreateMarkerSchema()
    {
        Schema? schema = Schema.Lookup(MarkerSchemaGuid);
        if (schema != null) return schema;

        var builder = new SchemaBuilder(MarkerSchemaGuid);
        builder.SetSchemaName("RevitFlexConduitControlPoint");
        builder.SetVendorId("CTRN");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("RunId", typeof(string));
        builder.AddSimpleField("Index", typeof(int));
        return builder.Finish();
    }

    private static Schema GetOrCreateSettingsSchema()
    {
        Schema? schema = Schema.Lookup(SettingsSchemaGuid);
        if (schema != null) return schema;

        var builder = new SchemaBuilder(SettingsSchemaGuid);
        builder.SetSchemaName("RevitFlexConduitSettings");
        builder.SetVendorId("CTRN");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("TypeId", typeof(string));
        builder.AddSimpleField("LevelId", typeof(string));
        builder.AddSimpleField("Diameter", typeof(double));
        builder.AddSimpleField("ServiceType", typeof(string));
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

    private static void WriteMarkerData(Element element, string runId, int index)
    {
        Schema schema = GetOrCreateMarkerSchema();
        var entity = new Entity(schema);
        entity.Set(schema.GetField("RunId"), runId);
        entity.Set(schema.GetField("Index"), index);
        element.SetEntity(entity);
    }

    private static void WriteSettings(Element element, ConduitSettings settings)
    {
        Schema schema = GetOrCreateSettingsSchema();
        var entity = new Entity(schema);
        entity.Set(schema.GetField("TypeId"), settings.TypeId.Value.ToString(CultureInfo.InvariantCulture));
        entity.Set(schema.GetField("LevelId"), settings.LevelId.Value.ToString(CultureInfo.InvariantCulture));
        entity.Set(schema.GetField("Diameter"), settings.Diameter);
        entity.Set(schema.GetField("ServiceType"), settings.ServiceType ?? string.Empty);
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
            return !string.IsNullOrWhiteSpace(runId) && controlPoints.Count >= 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadMarkerData(Element element, out string runId, out int index)
    {
        runId = string.Empty;
        index = -1;
        Schema? schema = Schema.Lookup(MarkerSchemaGuid);
        if (schema == null) return false;

        Entity entity = element.GetEntity(schema);
        if (!entity.IsValid()) return false;

        try
        {
            runId = entity.Get<string>(schema.GetField("RunId")) ?? string.Empty;
            index = entity.Get<int>(schema.GetField("Index"));
            return !string.IsNullOrWhiteSpace(runId) && index >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadSettings(Element element, out ConduitSettings settings)
    {
        settings = new ConduitSettings(ElementId.InvalidElementId, ElementId.InvalidElementId, DefaultDiameterFeet, string.Empty);
        Schema? schema = Schema.Lookup(SettingsSchemaGuid);
        if (schema == null) return false;

        Entity entity = element.GetEntity(schema);
        if (!entity.IsValid()) return false;

        try
        {
            string typeText = entity.Get<string>(schema.GetField("TypeId")) ?? "-1";
            string levelText = entity.Get<string>(schema.GetField("LevelId")) ?? "-1";
            double diameter = entity.Get<double>(schema.GetField("Diameter"));
            string service = entity.Get<string>(schema.GetField("ServiceType")) ?? string.Empty;

            long.TryParse(typeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long typeValue);
            long.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long levelValue);
            settings = new ConduitSettings(new ElementId(typeValue), new ElementId(levelValue), diameter > 0 ? diameter : DefaultDiameterFeet, service);
            return true;
        }
        catch
        {
            return false;
        }
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

    private static void CleanupCancelledNewRun(Document doc, FlexState state)
    {
        using var tx = new Transaction(doc, "Cancel Flex Conduit");
        tx.Start();
        DeleteElementsNoThrow(doc, state.SplineIds);
        DeleteElementsNoThrow(doc, state.MarkerToControlIndex.Keys);
        state.SplineIds = new List<ElementId>();
        state.MarkerToControlIndex = new Dictionary<ElementId, int>();
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

    private sealed class FlexState
    {
        public List<ElementId> SplineIds { get; set; } = new();
        public Dictionary<ElementId, int> MarkerToControlIndex { get; set; } = new();
    }

    private sealed record ConduitSettings(ElementId TypeId, ElementId LevelId, double Diameter, string ServiceType);
}
