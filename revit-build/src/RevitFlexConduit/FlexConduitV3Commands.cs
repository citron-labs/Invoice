using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitOperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace RevitFlexConduit;

internal enum FlexV3Tool
{
    EditPath,
    AddPoint,
    DeletePoint,
    Smooth,
    Reverse,
    Reconnect,
    SetDiameter,
    ConvertToConduit
}

internal sealed class FlexEndpointPick
{
    public XYZ Point { get; init; } = XYZ.Zero;
    public FlexConnectorBinding Binding { get; init; } = FlexConnectorBinding.Disconnected(XYZ.Zero);
    public Element? Owner { get; init; }
}

internal static class FlexV3Controller
{
    private static ObjectSnapTypes Snaps => ObjectSnapTypes.Endpoints |
                                                  ObjectSnapTypes.Midpoints |
                                                  ObjectSnapTypes.Intersections |
                                                  ObjectSnapTypes.Nearest |
                                                  ObjectSnapTypes.Points;

    internal static Result CreateOrEdit(ExternalCommandData commandData, ref string message)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        try
        {
            EnsureParameters(doc);
            if (TryGetSelectedRecord(uidoc, out FlexV3Record selected, out _))
                return ShowEditMenu(uidoc, selected);
            return CreateNew(uidoc);
        }
        catch (RevitOperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.ToString();
            TaskDialog.Show("Flex Conduit v3", "Flex Conduit could not complete the operation.\n\n" + ex.Message);
            return Result.Failed;
        }
    }

    internal static Result RunTool(ExternalCommandData commandData, FlexV3Tool tool, ref string message)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        try
        {
            EnsureParameters(uidoc.Document);
            if (!TryGetSelectedRecord(uidoc, out FlexV3Record record, out _))
            {
                TaskDialog.Show("Flex Conduit", "Select a Flex Conduit body or one of its control points first.");
                return Result.Cancelled;
            }

            return tool switch
            {
                FlexV3Tool.EditPath => EditPath(uidoc, record),
                FlexV3Tool.AddPoint => AddPoint(uidoc, record),
                FlexV3Tool.DeletePoint => DeletePoint(uidoc, record),
                FlexV3Tool.Smooth => Smooth(uidoc, record),
                FlexV3Tool.Reverse => Reverse(uidoc, record),
                FlexV3Tool.Reconnect => Reconnect(uidoc, record),
                FlexV3Tool.SetDiameter => SetDiameter(uidoc, record),
                FlexV3Tool.ConvertToConduit => ConvertToConduit(uidoc, record),
                _ => Result.Cancelled
            };
        }
        catch (RevitOperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.ToString();
            TaskDialog.Show("Flex Conduit", ex.Message);
            return Result.Failed;
        }
    }

    private static void EnsureParameters(Document doc)
    {
        if (doc.IsReadOnly) return;
        using var tx = new Transaction(doc, "Flex Conduit Project Parameters");
        tx.Start();
        FlexV3ParameterService.Ensure(doc);
        tx.Commit();
    }

    private static Result CreateNew(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        Conduit? template = uidoc.Selection.GetElementIds().Select(doc.GetElement).OfType<Conduit>().FirstOrDefault();
        FlexV3Settings settings = FlexV3Engine.CaptureSettings(doc, template, uidoc.ActiveView);

        FlexEndpointPick start = PickEndpoint(uidoc, "START");
        if (template == null && start.Owner is Conduit startConduit)
            settings = FlexV3Engine.CaptureSettings(doc, startConduit, uidoc.ActiveView);

        string runId = "FLEX-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var record = new FlexV3Record
        {
            RunId = runId,
            Kind = "Body",
            MarkerIndex = -1,
            Settings = settings,
            Start = start.Binding,
            End = FlexConnectorBinding.Disconnected(start.Point),
            Points = new List<FlexPointDto> { new(start.Point) }
        };

        bool createdPreview = false;
        try
        {
            while (true)
            {
                XYZ p = uidoc.Selection.PickPoint(
                    Snaps,
                    "Flex Conduit: click a control point. Press ESC when ready to choose the END connector/point.");
                List<XYZ> existing = record.XyzPoints;
                if (existing.Any(x => x.DistanceTo(p) < FlexV3Engine.MinSpacing)) continue;
                record.Points.Add(new FlexPointDto(p));
                if (record.Points.Count >= 2)
                {
                    record.End = FlexConnectorBinding.Disconnected(p);
                    UpdateRun(doc, record, uidoc.ActiveView);
                    createdPreview = true;
                }
            }
        }
        catch (RevitOperationCanceledException)
        {
            // ESC finishes control-point placement and advances to the end connection.
        }

        try
        {
            FlexEndpointPick end = PickEndpoint(uidoc, "END");
            List<XYZ> points = record.XyzPoints;
            if (points.Count == 1)
            {
                points.Add(end.Point);
                points.Insert(1, FlexV3Engine.AutoMiddle(points[0], points[^1], uidoc.ActiveView));
            }
            else
            {
                if (points[^1].DistanceTo(end.Point) < FlexV3Engine.MinSpacing)
                    points[^1] = end.Point;
                else
                    points.Add(end.Point);
            }

            record.Points = points.Select(x => new FlexPointDto(x)).ToList();
            record.End = end.Binding;
            UpdateRun(doc, record, uidoc.ActiveView);
            FlexV3Engine.RegisterRunTriggers(doc, record);
            SelectBody(uidoc, record.RunId);
            return Result.Succeeded;
        }
        catch (RevitOperationCanceledException)
        {
            if (createdPreview) DeleteRun(doc, record.RunId);
            return Result.Cancelled;
        }
    }

    private static FlexEndpointPick PickEndpoint(UIDocument uidoc, string which)
    {
        var td = new TaskDialog($"Flex Conduit — {which}")
        {
            MainInstruction = $"Choose the {which.ToLowerInvariant()} of the Flex Conduit",
            MainContent = "A connector keeps a persistent relationship to the equipment/conduit endpoint. A point remains free in model space.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.CommandLink1
        };
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Electrical connector / conduit endpoint");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Arbitrary XYZ point");
        TaskDialogResult result = td.Show();

        if (result == TaskDialogResult.CommandLink1)
        {
            Reference reference = uidoc.Selection.PickObject(
                ObjectType.Element,
                new FlexConnectorOwnerFilter(),
                $"Flex Conduit: select equipment, fitting, or conduit containing the {which} connector");
            Element owner = uidoc.Document.GetElement(reference.ElementId);
            XYZ near;
            try { near = reference.GlobalPoint; }
            catch { near = owner.get_BoundingBox(null)?.Min ?? XYZ.Zero; }
            Connector? connector = FlexConnectorUtil.FindNearest(owner, near);
            if (connector == null) throw new InvalidOperationException("The selected element does not expose an MEP connector.");
            return new FlexEndpointPick
            {
                Point = connector.Origin,
                Binding = FlexConnectorUtil.CreateBinding(owner, connector),
                Owner = owner
            };
        }

        if (result == TaskDialogResult.CommandLink2)
        {
            XYZ p = uidoc.Selection.PickPoint(Snaps, $"Flex Conduit: pick {which} point");
            return new FlexEndpointPick { Point = p, Binding = FlexConnectorBinding.Disconnected(p) };
        }

        throw new RevitOperationCanceledException();
    }

    private static Result ShowEditMenu(UIDocument uidoc, FlexV3Record record)
    {
        var td = new TaskDialog("Modify | Flex Conduit")
        {
            MainInstruction = "Edit the selected Flex Conduit",
            MainContent = $"Run {record.RunId} • {record.XyzPoints.Count} control points • length {FormatLength(record.SplineLength)}",
            CommonButtons = TaskDialogCommonButtons.Cancel
        };
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Edit Path", "Move any persistent XYZ control point.");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Add Control Point");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Delete Control Point");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "More tools", "Smooth, reverse, reconnect, diameter, convert.");
        TaskDialogResult result = td.Show();

        if (result == TaskDialogResult.CommandLink1) return EditPath(uidoc, record);
        if (result == TaskDialogResult.CommandLink2) return AddPoint(uidoc, record);
        if (result == TaskDialogResult.CommandLink3) return DeletePoint(uidoc, record);
        if (result == TaskDialogResult.CommandLink4) return ShowMoreTools(uidoc, record);
        return Result.Cancelled;
    }

    private static Result ShowMoreTools(UIDocument uidoc, FlexV3Record record)
    {
        var td = new TaskDialog("Flex Conduit Tools")
        {
            MainInstruction = "Additional Flex Conduit tools",
            CommonButtons = TaskDialogCommonButtons.Cancel
        };
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Reset / Smooth Route", "Keep endpoints and rebuild a clean smooth route.");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Reverse Direction");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Reconnect Endpoints");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "More", "Diameter or Convert to Conduit.");
        TaskDialogResult result = td.Show();
        if (result == TaskDialogResult.CommandLink1) return Smooth(uidoc, record);
        if (result == TaskDialogResult.CommandLink2) return Reverse(uidoc, record);
        if (result == TaskDialogResult.CommandLink3) return Reconnect(uidoc, record);
        if (result == TaskDialogResult.CommandLink4)
        {
            var more = new TaskDialog("Flex Conduit") { MainInstruction = "Geometry / conversion", CommonButtons = TaskDialogCommonButtons.Cancel };
            more.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Change Diameter");
            more.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Convert to native Conduit", "Approximate the spline with connected native conduit segments.");
            TaskDialogResult m = more.Show();
            if (m == TaskDialogResult.CommandLink1) return SetDiameter(uidoc, record);
            if (m == TaskDialogResult.CommandLink2) return ConvertToConduit(uidoc, record);
        }
        return Result.Cancelled;
    }

    private static Result EditPath(UIDocument uidoc, FlexV3Record record)
    {
        Document doc = uidoc.Document;
        while (true)
        {
            List<DirectShape> markers = FlexV3Data.FindMarkers(doc, record.RunId);
            try
            {
                Reference picked = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new IdFilter(markers.Select(m => m.Id)),
                    "Flex Conduit: select a control point to move, or ESC to finish");
                DirectShape marker = (DirectShape)doc.GetElement(picked.ElementId);
                if (!FlexV3Data.TryRead(marker, out FlexV3Record markerRecord)) continue;
                int index = markerRecord.MarkerIndex;
                XYZ newPoint = uidoc.Selection.PickPoint(Snaps, $"Move control point {index + 1} to new XYZ position");
                List<XYZ> pts = record.XyzPoints;
                if (index < 0 || index >= pts.Count) continue;
                pts[index] = newPoint;
                record.Points = pts.Select(x => new FlexPointDto(x)).ToList();
                if (index == 0) record.Start = FlexConnectorBinding.Disconnected(newPoint);
                if (index == pts.Count - 1) record.End = FlexConnectorBinding.Disconnected(newPoint);
                UpdateRun(doc, record, uidoc.ActiveView);
                FlexV3Engine.RegisterRunTriggers(doc, record);
            }
            catch (RevitOperationCanceledException)
            {
                SelectBody(uidoc, record.RunId);
                return Result.Succeeded;
            }
        }
    }

    private static Result AddPoint(UIDocument uidoc, FlexV3Record record)
    {
        XYZ p = uidoc.Selection.PickPoint(Snaps, "Flex Conduit: pick the new control point location");
        List<XYZ> points = record.XyzPoints;
        int index = FlexV3Engine.FindInsertionIndex(points, p);
        index = Math.Clamp(index, 1, points.Count - 1);
        points.Insert(index, p);
        record.Points = points.Select(x => new FlexPointDto(x)).ToList();
        UpdateRun(uidoc.Document, record, uidoc.ActiveView);
        FlexV3Engine.RegisterRunTriggers(uidoc.Document, record);
        SelectBody(uidoc, record.RunId);
        return Result.Succeeded;
    }

    private static Result DeletePoint(UIDocument uidoc, FlexV3Record record)
    {
        List<DirectShape> markers = FlexV3Data.FindMarkers(uidoc.Document, record.RunId);
        Reference picked = uidoc.Selection.PickObject(
            ObjectType.Element,
            new IdFilter(markers.Select(m => m.Id)),
            "Flex Conduit: select an INTERIOR control point to delete");
        DirectShape marker = (DirectShape)uidoc.Document.GetElement(picked.ElementId);
        if (!FlexV3Data.TryRead(marker, out FlexV3Record markerRecord)) return Result.Cancelled;
        int index = markerRecord.MarkerIndex;
        List<XYZ> points = record.XyzPoints;
        if (index <= 0 || index >= points.Count - 1)
        {
            TaskDialog.Show("Flex Conduit", "Start and end control points cannot be deleted. Use Reconnect or Edit Path instead.");
            return Result.Cancelled;
        }
        points.RemoveAt(index);
        if (points.Count == 2)
            points.Insert(1, FlexV3Engine.AutoMiddle(points[0], points[1], uidoc.ActiveView));
        record.Points = points.Select(x => new FlexPointDto(x)).ToList();
        UpdateRun(uidoc.Document, record, uidoc.ActiveView);
        FlexV3Engine.RegisterRunTriggers(uidoc.Document, record);
        SelectBody(uidoc, record.RunId);
        return Result.Succeeded;
    }

    private static Result Smooth(UIDocument uidoc, FlexV3Record record)
    {
        List<XYZ> points = record.XyzPoints;
        XYZ start = points[0];
        XYZ end = points[^1];
        record.Points = new List<FlexPointDto>
        {
            new(start),
            new(FlexV3Engine.AutoMiddle(start, end, uidoc.ActiveView)),
            new(end)
        };
        UpdateRun(uidoc.Document, record, uidoc.ActiveView);
        FlexV3Engine.RegisterRunTriggers(uidoc.Document, record);
        SelectBody(uidoc, record.RunId);
        return Result.Succeeded;
    }

    private static Result Reverse(UIDocument uidoc, FlexV3Record record)
    {
        List<XYZ> points = record.XyzPoints;
        points.Reverse();
        record.Points = points.Select(x => new FlexPointDto(x)).ToList();
        (record.Start, record.End) = (record.End, record.Start);
        UpdateRun(uidoc.Document, record, uidoc.ActiveView);
        FlexV3Engine.RegisterRunTriggers(uidoc.Document, record);
        SelectBody(uidoc, record.RunId);
        return Result.Succeeded;
    }

    private static Result Reconnect(UIDocument uidoc, FlexV3Record record)
    {
        var td = new TaskDialog("Reconnect Flex Conduit")
        {
            MainInstruction = "Which endpoint should be reconnected?",
            CommonButtons = TaskDialogCommonButtons.Cancel
        };
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Reconnect START");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Reconnect END");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Reconnect BOTH");
        TaskDialogResult result = td.Show();
        List<XYZ> points = record.XyzPoints;

        if (result is TaskDialogResult.CommandLink1 or TaskDialogResult.CommandLink3)
        {
            FlexEndpointPick start = PickConnectorOnly(uidoc, "START");
            points[0] = start.Point;
            record.Start = start.Binding;
        }
        if (result is TaskDialogResult.CommandLink2 or TaskDialogResult.CommandLink3)
        {
            FlexEndpointPick end = PickConnectorOnly(uidoc, "END");
            points[^1] = end.Point;
            record.End = end.Binding;
        }
        if (result == TaskDialogResult.Cancel) return Result.Cancelled;

        record.Points = points.Select(x => new FlexPointDto(x)).ToList();
        UpdateRun(uidoc.Document, record, uidoc.ActiveView);
        FlexV3Engine.RegisterRunTriggers(uidoc.Document, record);
        SelectBody(uidoc, record.RunId);
        return Result.Succeeded;
    }

    private static FlexEndpointPick PickConnectorOnly(UIDocument uidoc, string which)
    {
        Reference reference = uidoc.Selection.PickObject(
            ObjectType.Element,
            new FlexConnectorOwnerFilter(),
            $"Flex Conduit: select the element containing the new {which} connector");
        Element owner = uidoc.Document.GetElement(reference.ElementId);
        XYZ near;
        try { near = reference.GlobalPoint; } catch { near = owner.get_BoundingBox(null)?.Min ?? XYZ.Zero; }
        Connector? connector = FlexConnectorUtil.FindNearest(owner, near);
        if (connector == null) throw new InvalidOperationException("No valid connector found.");
        return new FlexEndpointPick { Point = connector.Origin, Binding = FlexConnectorUtil.CreateBinding(owner, connector), Owner = owner };
    }

    private static Result SetDiameter(UIDocument uidoc, FlexV3Record record)
    {
        var window = new FlexDiameterWindow(record.Settings.Diameter * 12.0);
        bool? result = window.ShowDialog();
        if (result != true) return Result.Cancelled;
        record.Settings.Diameter = window.DiameterInches / 12.0;
        UpdateRun(uidoc.Document, record, uidoc.ActiveView);
        FlexV3Engine.RegisterRunTriggers(uidoc.Document, record);
        SelectBody(uidoc, record.RunId);
        return Result.Succeeded;
    }

    private static Result ConvertToConduit(UIDocument uidoc, FlexV3Record record)
    {
        Document doc = uidoc.Document;
        HermiteSpline spline = FlexV3Engine.BuildSpline(record, uidoc.ActiveView);
        IList<XYZ> raw = spline.Tessellate();
        List<XYZ> points = SimplifyTessellation(raw, 0.35);
        if (points.Count < 2) return Result.Cancelled;

        ElementId typeId = record.Settings.TypeId >= 0 ? new ElementId(record.Settings.TypeId) : ElementId.InvalidElementId;
        if (doc.GetElement(typeId) is not ConduitType)
            typeId = new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).FirstElementId();
        ElementId levelId = record.Settings.LevelId >= 0 ? new ElementId(record.Settings.LevelId) : ElementId.InvalidElementId;
        if (doc.GetElement(levelId) is not Level)
            levelId = uidoc.ActiveView.GenLevel?.Id ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElementId();

        using var tx = new Transaction(doc, "Convert Flex Conduit to Native Conduit");
        tx.Start();
        var conduits = new List<Conduit>();
        for (int i = 0; i < points.Count - 1; i++)
        {
            if (points[i].DistanceTo(points[i + 1]) < 0.01) continue;
            Conduit c = Conduit.Create(doc, typeId, points[i], points[i + 1], levelId);
            FlexV3Engine.ApplyNativeConduitSettings(c, record.Settings);
            conduits.Add(c);
        }
        doc.Regenerate();
        ConnectNativeRun(conduits, record, doc);
        foreach (DirectShape marker in FlexV3Data.FindMarkers(doc, record.RunId)) doc.Delete(marker.Id);
        DirectShape? body = FlexV3Data.FindBody(doc, record.RunId);
        if (body != null) doc.Delete(body.Id);
        tx.Commit();
        uidoc.Selection.SetElementIds(conduits.Select(c => c.Id).ToList());
        return Result.Succeeded;
    }

    private static List<XYZ> SimplifyTessellation(IList<XYZ> raw, double targetFeet)
    {
        var result = new List<XYZ>();
        if (raw.Count == 0) return result;
        result.Add(raw[0]);
        foreach (XYZ p in raw.Skip(1).Take(Math.Max(0, raw.Count - 2)))
            if (result[^1].DistanceTo(p) >= targetFeet) result.Add(p);
        if (result[^1].DistanceTo(raw[^1]) > 1e-6) result.Add(raw[^1]);
        return result;
    }

    private static void ConnectNativeRun(List<Conduit> conduits, FlexV3Record record, Document doc)
    {
        for (int i = 0; i < conduits.Count - 1; i++)
        {
            XYZ joint = ((LocationCurve)conduits[i].Location).Curve.GetEndPoint(1);
            Connector? a = Closest(conduits[i], joint);
            Connector? b = Closest(conduits[i + 1], joint);
            try { if (a != null && b != null && !a.IsConnectedTo(b)) a.ConnectTo(b); } catch { }
        }

        if (conduits.Count == 0) return;
        if (FlexConnectorUtil.TryResolve(doc, record.Start, out _, out Connector? startExternal) && startExternal != null)
        {
            Connector? local = Closest(conduits[0], record.XyzPoints[0]);
            try { if (local != null && !local.IsConnectedTo(startExternal)) local.ConnectTo(startExternal); } catch { }
        }
        if (FlexConnectorUtil.TryResolve(doc, record.End, out _, out Connector? endExternal) && endExternal != null)
        {
            Connector? local = Closest(conduits[^1], record.XyzPoints[^1]);
            try { if (local != null && !local.IsConnectedTo(endExternal)) local.ConnectTo(endExternal); } catch { }
        }
    }

    private static Connector? Closest(Conduit conduit, XYZ point)
        => conduit.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(point)).FirstOrDefault();

    private static void UpdateRun(Document doc, FlexV3Record record, View view)
    {
        using var tx = new Transaction(doc, "Update Flex Conduit");
        tx.Start();
        FlexV3Engine.Regenerate(doc, record, view);
        tx.Commit();
    }

    private static void DeleteRun(Document doc, string runId)
    {
        using var tx = new Transaction(doc, "Cancel Flex Conduit");
        tx.Start();
        foreach (DirectShape marker in FlexV3Data.FindMarkers(doc, runId)) doc.Delete(marker.Id);
        DirectShape? body = FlexV3Data.FindBody(doc, runId);
        if (body != null) doc.Delete(body.Id);
        tx.Commit();
    }

    internal static bool TryGetSelectedRecord(UIDocument uidoc, out FlexV3Record record, out Element? selectedElement)
    {
        record = new FlexV3Record();
        selectedElement = null;
        foreach (ElementId id in uidoc.Selection.GetElementIds())
        {
            Element e = uidoc.Document.GetElement(id);
            if (!FlexV3Data.TryRead(e, out FlexV3Record candidate)) continue;
            selectedElement = e;
            if (candidate.Kind == "Body")
            {
                record = candidate;
                return true;
            }
            DirectShape? body = FlexV3Data.FindBody(uidoc.Document, candidate.RunId);
            if (body != null && FlexV3Data.TryRead(body, out FlexV3Record bodyRecord))
            {
                record = bodyRecord;
                return true;
            }
        }
        return false;
    }

    private static void SelectBody(UIDocument uidoc, string runId)
    {
        DirectShape? body = FlexV3Data.FindBody(uidoc.Document, runId);
        if (body != null) uidoc.Selection.SetElementIds(new[] { body.Id });
    }

    private static string FormatLength(double feet)
    {
        int wholeFeet = (int)Math.Floor(feet);
        double inches = (feet - wholeFeet) * 12.0;
        return string.Create(CultureInfo.InvariantCulture, $"{wholeFeet}'-{inches:0.##}\"");
    }

    private sealed class IdFilter : ISelectionFilter
    {
        private readonly HashSet<ElementId> _ids;
        internal IdFilter(IEnumerable<ElementId> ids) => _ids = ids.ToHashSet();
        public bool AllowElement(Element elem) => _ids.Contains(elem.Id);
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}

internal sealed class FlexDiameterWindow : Window
{
    private readonly TextBox _text = new();
    internal double DiameterInches { get; private set; }

    internal FlexDiameterWindow(double currentInches)
    {
        Title = "Flex Conduit Diameter";
        Width = 330;
        Height = 180;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Diameter / trade size (inches)", Margin = new Thickness(0, 0, 0, 6) });
        _text.Text = currentInches.ToString("0.###", CultureInfo.InvariantCulture);
        _text.MinHeight = 28;
        panel.Children.Add(_text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "OK", Width = 80, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, Height = 30, IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (!double.TryParse(_text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0)
            {
                MessageBox.Show(this, "Enter a positive diameter in inches.", "Flex Conduit");
                return;
            }
            DiameterInches = value;
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class FlexConduitV3Command : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        => FlexV3Controller.CreateOrEdit(commandData, ref message);
}

[Transaction(TransactionMode.Manual)] public sealed class FlexEditPathCommand : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => FlexV3Controller.RunTool(d, FlexV3Tool.EditPath, ref m); }
[Transaction(TransactionMode.Manual)] public sealed class FlexAddPointCommand : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => FlexV3Controller.RunTool(d, FlexV3Tool.AddPoint, ref m); }
[Transaction(TransactionMode.Manual)] public sealed class FlexDeletePointCommand : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => FlexV3Controller.RunTool(d, FlexV3Tool.DeletePoint, ref m); }
[Transaction(TransactionMode.Manual)] public sealed class FlexSmoothCommand : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => FlexV3Controller.RunTool(d, FlexV3Tool.Smooth, ref m); }
[Transaction(TransactionMode.Manual)] public sealed class FlexReverseCommand : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => FlexV3Controller.RunTool(d, FlexV3Tool.Reverse, ref m); }
[Transaction(TransactionMode.Manual)] public sealed class FlexReconnectCommand : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => FlexV3Controller.RunTool(d, FlexV3Tool.Reconnect, ref m); }
[Transaction(TransactionMode.Manual)] public sealed class FlexDiameterCommand : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => FlexV3Controller.RunTool(d, FlexV3Tool.SetDiameter, ref m); }
[Transaction(TransactionMode.Manual)] public sealed class FlexConvertCommand : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) => FlexV3Controller.RunTool(d, FlexV3Tool.ConvertToConduit, ref m); }

public sealed class FlexV3Availability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        => applicationData.ActiveUIDocument?.Document != null;
}

public sealed class FlexV3SelectedAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
    {
        UIDocument? uidoc = applicationData.ActiveUIDocument;
        return uidoc != null && FlexV3Controller.TryGetSelectedRecord(uidoc, out _, out _);
    }
}
