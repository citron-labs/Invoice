using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitFlexConduit;

internal sealed class FlexConduitV3Updater : IUpdater
{
    private static readonly Guid UpdaterGuid = new("CF3AF50E-A568-418A-942E-8C75D8758D18");
    private static readonly AddInId AddinId = new(new Guid("4DBA337A-4F70-4B8B-A6EF-0D4DA6A29C55"));
    private static readonly FlexConduitV3Updater Instance = new();
    private static bool _registered;
    private static bool _executing;

    private readonly UpdaterId _updaterId = new(AddinId, UpdaterGuid);

    internal static void RegisterApplicationUpdater()
    {
        if (_registered) return;
        try
        {
            UpdaterRegistry.RegisterUpdater(Instance, true);
            _registered = true;
        }
        catch
        {
            // A hot reload or duplicate startup can leave the updater registered already.
            _registered = true;
        }
    }

    internal static void UnregisterApplicationUpdater()
    {
        if (!_registered) return;
        try { UpdaterRegistry.UnregisterUpdater(Instance._updaterId); } catch { }
        _registered = false;
    }

    internal static void RegisterElementTriggers(Document doc, IEnumerable<ElementId> elementIds)
    {
        RegisterApplicationUpdater();
        List<ElementId> ids = elementIds
            .Where(id => id != ElementId.InvalidElementId && doc.GetElement(id) != null)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return;

        try { UpdaterRegistry.AddTrigger(Instance._updaterId, doc, ids, Element.GetChangeTypeAny()); } catch { }
        try { UpdaterRegistry.AddTrigger(Instance._updaterId, doc, ids, Element.GetChangeTypeElementDeletion()); } catch { }
    }

    internal static void RegisterExisting(Document doc)
    {
        RegisterApplicationUpdater();
        foreach (DirectShape body in FlexV3Data.AllBodies(doc).ToList())
        {
            if (!FlexV3Data.TryRead(body, out FlexV3Record record)) continue;
            FlexV3Engine.RegisterRunTriggers(doc, record);
        }
    }

    public void Execute(UpdaterData data)
    {
        if (_executing) return;
        _executing = true;
        try
        {
            Document doc = data.GetDocument();
            List<ElementId> modified = data.GetModifiedElementIds().Concat(data.GetAddedElementIds()).Distinct().ToList();
            HashSet<long> changedIds = modified.Select(id => id.Value).ToHashSet();
            HashSet<long> deletedIds = data.GetDeletedElementIds().Select(id => id.Value).ToHashSet();

            // First handle directly moved control points and property edits on the Flex body.
            foreach (ElementId id in modified)
            {
                Element? element = doc.GetElement(id);
                if (!FlexV3Data.TryRead(element, out FlexV3Record elementRecord)) continue;

                if (elementRecord.Kind == "Marker")
                {
                    DirectShape? body = FlexV3Data.FindBody(doc, elementRecord.RunId);
                    if (body == null || !FlexV3Data.TryRead(body, out FlexV3Record record)) continue;
                    int index = elementRecord.MarkerIndex;
                    List<XYZ> points = record.XyzPoints;
                    if (index < 0 || index >= points.Count) continue;

                    XYZ center = FlexV3Engine.MarkerCenter(element!);
                    if (center.DistanceTo(points[index]) < 1e-7) continue;
                    points[index] = center;
                    record.Points = points.Select(p => new FlexPointDto(p)).ToList();
                    if (index == 0) record.Start = FlexConnectorBinding.Disconnected(center);
                    if (index == points.Count - 1) record.End = FlexConnectorBinding.Disconnected(center);
                    FlexV3Engine.Regenerate(doc, record, doc.ActiveView, false);
                    continue;
                }

                if (elementRecord.Kind == "Body")
                {
                    FlexV3Record record = elementRecord;
                    FlexV3ParameterService.TryReadEditableProperties(element!, record, out bool geometryChanged);
                    if (geometryChanged)
                        FlexV3Engine.Regenerate(doc, record, doc.ActiveView, false);
                    else
                        FlexV3Data.Write(element!, record);
                }
            }

            // Then update endpoint positions/directions when connected equipment or conduit moves.
            foreach (DirectShape body in FlexV3Data.AllBodies(doc).ToList())
            {
                if (!FlexV3Data.TryRead(body, out FlexV3Record record)) continue;

                bool ownerChanged =
                    (record.Start.Connected && changedIds.Contains(record.Start.OwnerId)) ||
                    (record.End.Connected && changedIds.Contains(record.End.OwnerId));
                bool ownerDeleted =
                    (record.Start.Connected && deletedIds.Contains(record.Start.OwnerId)) ||
                    (record.End.Connected && deletedIds.Contains(record.End.OwnerId));

                if (ownerDeleted)
                {
                    if (deletedIds.Contains(record.Start.OwnerId)) record.Start.Connected = false;
                    if (deletedIds.Contains(record.End.OwnerId)) record.End.Connected = false;
                    FlexV3Engine.Regenerate(doc, record, doc.ActiveView, false);
                }
                else if (ownerChanged)
                {
                    FlexV3Engine.RefreshConnectorBinding(doc, record);
                    FlexV3Engine.Regenerate(doc, record, doc.ActiveView, false);
                }
            }
        }
        catch
        {
            // Dynamic update must never interrupt a Revit transaction initiated by the user.
        }
        finally
        {
            _executing = false;
        }
    }

    public string GetAdditionalInformation() => "Keeps Revit Flex Conduit v3 splines, connectors, diameter, and persistent control points synchronized.";
    public ChangePriority GetChangePriority() => ChangePriority.MEPAccessoriesFittingsSegmentsWires;
    public UpdaterId GetUpdaterId() => _updaterId;
    public string GetUpdaterName() => "Revit Flex Conduit v3 Updater";
}
