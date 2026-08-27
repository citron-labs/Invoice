using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitOperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace RevitFlexConduit;

/// <summary>
/// Native-style Flex Conduit creation entry point.
/// START and END are selected with one normal Revit point pick: when the picked
/// XYZ coincides with an open conduit/MEP connector the endpoint binds to that
/// connector; otherwise the exact picked XYZ is stored as a free endpoint.
/// No endpoint-mode TaskDialog is shown.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class FlexConduitV301Command : IExternalCommand
{
    private const double ConnectorHitTolerance = 0.012; // ft, ~ 1/8 in
    private const double ConnectorSearchRadius = 0.15;  // ft, bounding-box search only

    private static ObjectSnapTypes Snaps => ObjectSnapTypes.Endpoints |
                                                  ObjectSnapTypes.Midpoints |
                                                  ObjectSnapTypes.Intersections |
                                                  ObjectSnapTypes.Nearest |
                                                  ObjectSnapTypes.Points;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        try
        {
            if (FlexV3Controller.TryGetSelectedRecord(uidoc, out _, out _))
                return FlexV3Controller.CreateOrEdit(commandData, ref message);

            EnsureParameters(uidoc.Document);
            return CreateNew(uidoc);
        }
        catch (RevitOperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.ToString();
            TaskDialog.Show($"Flex Conduit v{App301.ProductVersion}", "Flex Conduit could not complete the operation.\n\n" + ex.Message);
            return Result.Failed;
        }
    }

    private static void EnsureParameters(Document doc)
    {
        if (doc.IsReadOnly || doc.IsFamilyDocument) return;
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

        bool previewExists = false;
        try
        {
            while (true)
            {
                XYZ p = uidoc.Selection.PickPoint(
                    Snaps,
                    "Flex Conduit: click control points. The spline updates after each click. Press ESC when ready to place END.");

                if (record.XyzPoints.Any(x => x.DistanceTo(p) < FlexV3Engine.MinSpacing))
                    continue;

                record.Points.Add(new FlexPointDto(p));
                if (record.Points.Count >= 2)
                {
                    record.End = FlexConnectorBinding.Disconnected(p);
                    UpdateRun(doc, record, uidoc.ActiveView);
                    previewExists = true;
                }
            }
        }
        catch (RevitOperationCanceledException)
        {
            // ESC completes intermediate control-point placement.
        }

        FlexEndpointPick end;
        try
        {
            end = PickEndpoint(uidoc, "END");
        }
        catch (RevitOperationCanceledException)
        {
            if (previewExists) DeleteRun(doc, runId);
            return Result.Cancelled;
        }

        List<XYZ> points = record.XyzPoints;
        if (points.Count == 1)
        {
            points.Add(end.Point);
            points.Insert(1, FlexV3Engine.AutoMiddle(points[0], points[^1], uidoc.ActiveView));
        }
        else if (points[^1].DistanceTo(end.Point) < FlexV3Engine.MinSpacing)
        {
            points[^1] = end.Point;
        }
        else
        {
            points.Add(end.Point);
        }

        record.Points = points.Select(x => new FlexPointDto(x)).ToList();
        record.End = end.Binding;
        UpdateRun(doc, record, uidoc.ActiveView);
        FlexV3Engine.RegisterRunTriggers(doc, record);
        SelectBody(uidoc, runId);
        return Result.Succeeded;
    }

    private static FlexEndpointPick PickEndpoint(UIDocument uidoc, string which)
    {
        XYZ picked = uidoc.Selection.PickPoint(
            Snaps,
            $"Flex Conduit: click {which}. Snap to an open conduit/electrical connector to connect; click empty space for a free endpoint.");

        if (TryResolveConnectorAtPoint(uidoc, picked, out Element? owner, out Connector? connector) && owner != null && connector != null)
        {
            return new FlexEndpointPick
            {
                Point = connector.Origin,
                Binding = FlexConnectorUtil.CreateBinding(owner, connector),
                Owner = owner
            };
        }

        return new FlexEndpointPick
        {
            Point = picked,
            Binding = FlexConnectorBinding.Disconnected(picked)
        };
    }

    private static bool TryResolveConnectorAtPoint(UIDocument uidoc, XYZ picked, out Element? owner, out Connector? connector)
    {
        owner = null;
        connector = null;

        List<ConnectorHit> hits = new();
        foreach (Element element in FindNearbyElements(uidoc, picked))
        {
            foreach (Connector candidate in FlexConnectorUtil.GetConnectors(element))
            {
                double distance;
                try { distance = candidate.Origin.DistanceTo(picked); }
                catch { continue; }

                if (distance > ConnectorHitTolerance) continue;
                if (!IsOpenConnector(candidate)) continue;

                int priority = element is Conduit ? 0 :
                    element.Category?.Id.Value == (long)BuiltInCategory.OST_ConduitFitting ? 1 : 2;
                hits.Add(new ConnectorHit(element, candidate, distance, priority));
            }
        }

        ConnectorHit? best = hits
            .OrderBy(h => h.Priority)
            .ThenBy(h => h.Distance)
            .FirstOrDefault();

        if (best == null) return false;
        owner = best.Owner;
        connector = best.Connector;
        return true;
    }

    private static IEnumerable<Element> FindNearbyElements(UIDocument uidoc, XYZ picked)
    {
        Document doc = uidoc.Document;
        XYZ delta = new(ConnectorSearchRadius, ConnectorSearchRadius, ConnectorSearchRadius);
        var outline = new Outline(picked - delta, picked + delta);
        var bbox = new BoundingBoxIntersectsFilter(outline);

        try
        {
            return new FilteredElementCollector(doc, uidoc.ActiveView.Id)
                .WhereElementIsNotElementType()
                .WherePasses(bbox)
                .ToElements();
        }
        catch
        {
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(bbox)
                .ToElements();
        }
    }

    private static bool IsOpenConnector(Connector connector)
    {
        try
        {
            if (connector.IsConnected) return false;
            return connector.ConnectorType == ConnectorType.End ||
                   connector.ConnectorType == ConnectorType.Physical ||
                   connector.ConnectorType == ConnectorType.Curve;
        }
        catch
        {
            return false;
        }
    }

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
        foreach (DirectShape marker in FlexV3Data.FindMarkers(doc, runId))
            doc.Delete(marker.Id);
        DirectShape? body = FlexV3Data.FindBody(doc, runId);
        if (body != null) doc.Delete(body.Id);
        tx.Commit();
    }

    private static void SelectBody(UIDocument uidoc, string runId)
    {
        DirectShape? body = FlexV3Data.FindBody(uidoc.Document, runId);
        if (body != null)
            uidoc.Selection.SetElementIds(new[] { body.Id });
    }

    private sealed record ConnectorHit(Element Owner, Connector Connector, double Distance, int Priority);
}
