using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitFlexConduit;

/// <summary>
/// Bridges the plugin-owned spline to Revit's native electrical connector model.
/// Each connected Flex endpoint owns a short native Conduit anchor. The outer
/// connector of the anchor is physically connected to the selected conduit,
/// fitting, or equipment connector; the inner connector is the spline endpoint.
/// Because the anchors are real Conduit elements, Revit exposes its ordinary
/// conduit endpoint grips and connector behavior at the ends of a Flex run.
/// </summary>
internal static class FlexNativeEndpointService
{
    internal const string StartKind = "StartAnchor";
    internal const string EndKind = "EndAnchor";

    private const double MinAnchorLength = 0.15; // ft
    private const double MaxAnchorLength = 0.50; // ft
    private const double RebuildTolerance = 0.08; // ft

    internal static void SyncAnchors(Document doc, FlexV3Record record, View? view)
    {
        if (record.XyzPoints.Count < 2) return;

        Conduit? startExisting = FindAnchor(doc, record.RunId, StartKind);
        Conduit? endExisting = FindAnchor(doc, record.RunId, EndKind);

        // On first conversion from the older DirectShape-only implementation,
        // inherit native electrical properties from the attached conduit run.
        if (startExisting == null && endExisting == null)
            InheritInitialSettings(doc, record, view);

        SyncOne(doc, record, true, startExisting);
        SyncOne(doc, record, false, endExisting);
    }

    private static void InheritInitialSettings(Document doc, FlexV3Record record, View? view)
    {
        Conduit? source = FindConnectedSourceConduit(doc, record.Start)
            ?? FindConnectedSourceConduit(doc, record.End);
        if (source == null) return;

        View useView = view ?? doc.ActiveView;
        FlexV3Settings captured = FlexV3Engine.CaptureSettings(doc, source, useView);
        record.Settings.TypeId = captured.TypeId;
        record.Settings.TypeName = captured.TypeName;
        record.Settings.LevelId = captured.LevelId;
        record.Settings.LevelName = captured.LevelName;
        record.Settings.Diameter = captured.Diameter;
        record.Settings.ServiceType = captured.ServiceType;
        record.Settings.SystemName = captured.SystemName;
        record.Settings.Material = captured.Material;
        record.Settings.Workset = captured.Workset;
    }

    private static void SyncOne(Document doc, FlexV3Record record, bool isStart, Conduit? anchor)
    {
        FlexConnectorBinding binding = isStart ? record.Start : record.End;
        string kind = isStart ? StartKind : EndKind;

        if (!binding.Connected || !FlexConnectorUtil.TryResolve(doc, binding, out Element? owner, out Connector? external) || external == null)
        {
            if (anchor != null) doc.Delete(anchor.Id);
            return;
        }

        List<XYZ> points = record.XyzPoints;
        int pointIndex = isStart ? 0 : points.Count - 1;
        int interiorIndex = isStart ? Math.Min(1, points.Count - 1) : Math.Max(0, points.Count - 2);
        XYZ externalOrigin = external.Origin;
        XYZ interiorTarget = points[interiorIndex];

        if (anchor != null)
        {
            List<Connector> ac = AnchorConnectors(anchor);
            if (ac.Count < 2 || ac.Min(c => c.Origin.DistanceTo(externalOrigin)) > RebuildTolerance)
            {
                doc.Delete(anchor.Id);
                anchor = null;
            }
        }

        if (anchor == null)
        {
            ElementId typeId = ResolveTypeId(doc, record.Settings.TypeId);
            ElementId levelId = ResolveLevelId(doc, record.Settings.LevelId);
            XYZ direction = FlexConnectorUtil.GetDirection(external);
            if (direction.GetLength() < 1e-8) direction = (interiorTarget - externalOrigin).Normalize();
            XYZ toward = interiorTarget - externalOrigin;
            if (toward.GetLength() > 1e-8 && direction.DotProduct(toward.Normalize()) < 0)
                direction = -direction;

            double length = Math.Clamp(record.Settings.Diameter * 2.0, MinAnchorLength, MaxAnchorLength);
            XYZ inner = externalOrigin + direction.Normalize().Multiply(length);
            if (inner.DistanceTo(externalOrigin) < 0.01)
                inner = externalOrigin + XYZ.BasisX.Multiply(MinAnchorLength);

            anchor = Conduit.Create(doc, typeId, externalOrigin, inner, levelId);
            FlexV3Engine.ApplyNativeConduitSettings(anchor, record.Settings);
            doc.Regenerate();
            ConnectAnchor(doc, anchor, external);
        }
        else
        {
            FlexV3Engine.ApplyNativeConduitSettings(anchor, record.Settings);
            EnsureConnected(doc, anchor, external);
        }

        List<Connector> connectors = AnchorConnectors(anchor);
        if (connectors.Count < 2) return;
        Connector outer = connectors.OrderBy(c => c.Origin.DistanceTo(externalOrigin)).First();
        Connector innerConnector = connectors.OrderByDescending(c => c.Origin.DistanceTo(externalOrigin)).First();
        XYZ innerPoint = innerConnector.Origin;

        // The native anchor direction becomes the spline endpoint tangent. This
        // makes dragging the free anchor end reshape the spline without a kink.
        XYZ nativeDirection = innerPoint - outer.Origin;
        FlexConnectorBinding refreshed = FlexConnectorUtil.CreateBinding(owner!, external);
        if (nativeDirection.GetLength() > 1e-8)
            refreshed.Direction = new FlexPointDto(nativeDirection.Normalize());

        points[pointIndex] = innerPoint;
        record.Points = points.Select(p => new FlexPointDto(p)).ToList();
        if (isStart) record.Start = refreshed; else record.End = refreshed;

        FlexV3Record anchorRecord = record.Clone();
        anchorRecord.Kind = kind;
        anchorRecord.MarkerIndex = -1;
        FlexV3Data.Write(anchor, anchorRecord);
        FlexConduitV3Updater.RegisterElementTriggers(doc, new[] { anchor.Id });
    }

    private static void ConnectAnchor(Document doc, Conduit anchor, Connector external)
    {
        Connector? local = AnchorConnectors(anchor).OrderBy(c => c.Origin.DistanceTo(external.Origin)).FirstOrDefault();
        if (local == null) return;

        // For conduit-to-conduit endpoints prefer a native union fitting so the
        // result reads and behaves like an ordinary Revit raceway connection.
        if (external.Owner is Conduit)
        {
            try
            {
                doc.Create.NewUnionFitting(local, external);
                return;
            }
            catch { }
        }

        try
        {
            if (!local.IsConnectedTo(external)) local.ConnectTo(external);
        }
        catch { }
    }

    private static void EnsureConnected(Document doc, Conduit anchor, Connector external)
    {
        Connector? local = AnchorConnectors(anchor).OrderBy(c => c.Origin.DistanceTo(external.Origin)).FirstOrDefault();
        if (local == null || local.IsConnectedTo(external)) return;
        ConnectAnchor(doc, anchor, external);
    }

    internal static Conduit? FindAnchor(Document doc, string runId, string kind)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(Conduit))
            .Cast<Conduit>()
            .FirstOrDefault(c => FlexV3Data.TryRead(c, out FlexV3Record r) &&
                                 r.RunId == runId &&
                                 string.Equals(r.Kind, kind, StringComparison.Ordinal));

    internal static bool IsAnchor(Element? element, out string runId)
    {
        runId = string.Empty;
        if (element is not Conduit || !FlexV3Data.TryRead(element, out FlexV3Record r)) return false;
        if (r.Kind != StartKind && r.Kind != EndKind) return false;
        runId = r.RunId;
        return true;
    }

    internal static void DeleteAnchors(Document doc, string runId)
    {
        Conduit? start = FindAnchor(doc, runId, StartKind);
        Conduit? end = FindAnchor(doc, runId, EndKind);
        if (start != null) doc.Delete(start.Id);
        if (end != null && (start == null || end.Id != start.Id)) doc.Delete(end.Id);
    }

    internal static void SyncGraphics(Document doc, DirectShape body, FlexV3Record record)
    {
        Element? source = FindGraphicsSource(doc, record);
        if (source == null) return;

        foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            if (view.IsTemplate) continue;
            try
            {
                OverrideGraphicSettings effective = view.GetElementOverrides(source.Id);
                foreach (ElementId filterId in view.GetOrderedFilters())
                {
                    bool visible;
                    try { visible = view.GetFilterVisibility(filterId); }
                    catch { visible = true; }
                    if (!visible) continue;

                    if (doc.GetElement(filterId) is not ParameterFilterElement parameterFilter) continue;
                    ElementFilter filter = parameterFilter.GetElementFilter();
                    bool passes;
                    try { passes = filter.PassesFilter(doc, source.Id); }
                    catch { passes = false; }
                    if (!passes) continue;

                    effective = view.GetFilterOverrides(filterId);
                }
                view.SetElementOverrides(body.Id, effective);
            }
            catch
            {
                // Some non-graphical view types do not support element overrides.
            }
        }
    }

    private static Element? FindGraphicsSource(Document doc, FlexV3Record record)
    {
        Conduit? source = FindConnectedSourceConduit(doc, record.Start)
            ?? FindConnectedSourceConduit(doc, record.End);
        if (source != null) return source;
        return FindAnchor(doc, record.RunId, StartKind) ?? FindAnchor(doc, record.RunId, EndKind);
    }

    private static Conduit? FindConnectedSourceConduit(Document doc, FlexConnectorBinding binding)
    {
        if (!binding.Connected || !FlexConnectorUtil.TryResolve(doc, binding, out Element? owner, out Connector? connector) || connector == null)
            return null;
        if (owner is Conduit direct) return direct;

        // Prefer a conduit physically connected to the selected connector.
        foreach (Connector r in connector.AllRefs.Cast<Connector>())
            if (r.Owner is Conduit c) return c;

        // Fittings may be selected on an open connector. In that case inspect
        // the fitting's other connector references for the conduit feeding it.
        if (owner != null)
        {
            foreach (Connector c in FlexConnectorUtil.GetConnectors(owner))
                foreach (Connector r in c.AllRefs.Cast<Connector>())
                    if (r.Owner is Conduit conduit) return conduit;
        }
        return null;
    }

    private static List<Connector> AnchorConnectors(Conduit anchor)
        => anchor.ConnectorManager.Connectors.Cast<Connector>().ToList();

    private static ElementId ResolveTypeId(Document doc, long stored)
    {
        ElementId id = stored >= 0 ? new ElementId(stored) : ElementId.InvalidElementId;
        if (doc.GetElement(id) is ConduitType) return id;
        return new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).FirstElementId();
    }

    private static ElementId ResolveLevelId(Document doc, long stored)
    {
        ElementId id = stored >= 0 ? new ElementId(stored) : ElementId.InvalidElementId;
        if (doc.GetElement(id) is Level) return id;
        ElementId fallback = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElementId();
        if (fallback == ElementId.InvalidElementId)
            throw new InvalidOperationException("Flex Conduit requires at least one Revit Level.");
        return fallback;
    }
}
