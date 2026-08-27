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
/// v3.0.1 creation entry point. Revit 2025 throws an ArgumentException when a
/// TaskDialog DefaultButton is assigned to a command link before that link has
/// been registered. This command uses the same v3 data/geometry/updater engine
/// but deliberately leaves command-link dialogs without a DefaultButton.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class FlexConduitV301Command : IExternalCommand
{
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
            // Existing runs can continue to use the v3 edit controller; its edit dialogs
            // do not assign an invalid DefaultButton.
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
            TaskDialog.Show("Flex Conduit v3.0.1", "Flex Conduit could not complete the operation.\n\n" + ex.Message);
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

        FlexEndpointPick? start = PickEndpoint(uidoc, "START");
        if (start == null) return Result.Cancelled;
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
                    "Flex Conduit: click control points. The spline updates after every click. Press ESC when ready to choose the END.");

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
            // ESC finishes intermediate point placement and moves to END selection.
        }

        FlexEndpointPick? end = PickEndpoint(uidoc, "END");
        if (end == null)
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

    private static FlexEndpointPick? PickEndpoint(UIDocument uidoc, string which)
    {
        var td = new TaskDialog($"Flex Conduit — {which}")
        {
            MainInstruction = $"Choose the {which.ToLowerInvariant()} of the Flex Conduit",
            MainContent = "Connector mode keeps a persistent relationship to equipment or a conduit endpoint. Free point mode remains an independent XYZ endpoint.",
            CommonButtons = TaskDialogCommonButtons.Cancel
        };

        // Important: no DefaultButton assignment here. Revit 2025 validates the button
        // immediately and throws if the referenced command link has not yet been registered.
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
            XYZ near = GetReferencePoint(reference, owner);
            Connector? connector = FlexConnectorUtil.FindNearest(owner, near);
            if (connector == null)
                throw new InvalidOperationException("The selected element does not expose an MEP connector.");

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
            return new FlexEndpointPick
            {
                Point = p,
                Binding = FlexConnectorBinding.Disconnected(p)
            };
        }

        return null;
    }

    private static XYZ GetReferencePoint(Reference reference, Element owner)
    {
        try
        {
            XYZ? p = reference.GlobalPoint;
            if (p != null) return p;
        }
        catch { }

        BoundingBoxXYZ? box = owner.get_BoundingBox(null);
        return box == null ? XYZ.Zero : (box.Min + box.Max).Multiply(0.5);
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
}
