using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitFlexConduit;

/// <summary>
/// Bridges the plugin-owned spline to Revit's native electrical connector model.
/// Connected Flex endpoints can own a short native Conduit anchor. Topology-changing
/// work is deliberately suppressed while an IUpdater is executing; updaters only
/// read existing anchor geometry and reshape the spline. This prevents Revit from
/// cancelling user transactions because a third-party updater tried to create or
/// reconfigure conduit fittings during dynamic update.
/// </summary>
internal static class FlexNativeEndpointService
{
    internal const string StartKind = "StartAnchor";
    internal const string EndKind = "EndAnchor";

    private const double MinAnchorLength = 0.15; // ft
    private const double MaxAnchorLength = 0.50; // ft
    private const double RebuildTolerance = 0.08; // ft
    private const double ConnectorCoincidenceTolerance = 0.002; // ft

    internal static void SyncAnchors(Document doc, FlexV3Record record, View? view)
    {
        if (record.XyzPoints.Count < 2) return;

        Conduit? startExisting = FindAnchor(doc, record.RunId, StartKind);
        Conduit? endExisting = FindAnchor(doc, record.RunId, EndKind);

        // Revit's dynamic updater context is intentionally read-only with respect
        // to MEP topology. Creating/deleting conduit or fittings here can cause the
        // host transaction to be cancelled with "insufficient space" failures.
        if (FlexConduitV3Updater.IsExecuting)
        {
            SyncFromExistingAnchor(doc, record, true, startExisting);
            SyncFromExistingAnchor(doc, record, false, endExisting);
            return;
        }

        if (startExisting == null && endExisting == null)
            InheritInitialSettings(doc, record, view);

        SyncOne(doc, record, true, startExisting);
        SyncOne(doc, record, false, endExisting);
    }

    private static void SyncFromExistingAnchor(Document doc, FlexV3Record record, bool isStart, Conduit? anchor)
    {
        if (anchor == null) return;
        List<Connector> connectors = AnchorConnectors(anchor);
        if (connectors.Count < 2) return;

        FlexConnectorBinding binding = isStart ? record.Start : record.End;
        XYZ expectedOuter = binding.Origin.ToXyz();
        Element? owner = null;
        Connector? external = null;
        if (binding.Connected && FlexConnectorUtil.TryResolve(doc, binding, out owner, out external) && external != null)
            expectedOuter = external.Origin;

        Connector outer = connectors.OrderBy(c => c.Origin.DistanceTo(expectedOuter)).First();
        Connector inner = connectors.OrderByDescending(c => c.Origin.DistanceTo(expectedOuter)).First();

        List<XYZ> points = record.XyzPoints;
        int pointIndex = isStart ? 0 : points.Count - 1;
        points[pointIndex] = inner.Origin;
        record.Points = points.Select(p => new FlexPointDto(p)).ToList();

        if (owner != null && external != null)
        {
            FlexConnectorBinding refreshed = FlexConnectorUtil.CreateBinding(owner, external);
            XYZ direction = inner.Origin - outer.Origin;
            if (direction.GetLength() > 1e-8)
                refreshed.Direction = new FlexPointDto(direction.Normalize());
            if (isStart) record.Start = refreshed; else record.End = refreshed;
        }
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

        bool alreadyConnectedToAnchor = anchor != null && IsConnectedToAnchor(external, anchor);
        if (IsConnectorOccupied(external) && !alreadyConnectedToAnchor)
        {
            // Do not break an existing Revit MEP connection just to attach Flex.
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
            XYZ toward = interiorTarget - externalOrigin;

            if (direction.GetLength() < 1e-8)
            {
                direction = toward.GetLength() > 1e-8 ? toward.Normalize() : XYZ.BasisX;
            }
            else
            {
                direction = direction.Normalize();
                if (toward.GetLength() > 1e-8 && direction.DotProduct(toward.Normalize()) < 0)
                    direction = -direction;
            }

            double length = Math.Clamp(record.Settings.Diameter * 2.0, MinAnchorLength, MaxAnchorLength);
            XYZ inner = externalOrigin + direction.Multiply(length);
            if (inner.DistanceTo(externalOrigin) < 0.01)
                inner = externalOrigin + XYZ.BasisX.Multiply(MinAnchorLength);

            anchor = Conduit.Create(doc, typeId, externalOrigin, inner, levelId);
            FlexV3Engine.ApplyNativeConduitSettings(anchor, record.Settings);
            doc.Regenerate();
            ConnectAnchor(anchor, external);
        }
        else
        {
            FlexV3Engine.ApplyNativeConduitSettings(anchor, record.Settings);
            EnsureConnected(anchor, external);
        }

        List<Connector> connectors = AnchorConnectors(anchor);
        if (connectors.Count < 2) return;
        Connector outer = connectors.OrderBy(c => c.Origin.DistanceTo(externalOrigin)).First();
        Connector innerConnector = connectors.OrderByDescending(c => c.Origin.DistanceTo(externalOrigin)).First();
        XYZ innerPoint = innerConnector.Origin;

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

    private static void ConnectAnchor(Conduit anchor, Connector external)
    {
        Connector? local = AnchorConnectors(anchor).OrderBy(c => c.Origin.DistanceTo(external.Origin)).FirstOrDefault();
        if (local == null) return;
        if (SafeIsConnectedTo(local, external)) return;
        if (IsConnectorOccupied(external)) return;

        // Never force NewUnionFitting here. Explicit fitting creation can fail for
        // short/reversed conduit and can poison the surrounding Revit transaction.
        // Coincident, collinear connectors are connected directly; if Revit rejects
        // it the Flex binding remains valid without cancelling the user's operation.
        if (!CanDirectConnect(local, external)) return;
        try { local.ConnectTo(external); } catch { }
    }

    private static void EnsureConnected(Conduit anchor, Connector external)
    {
        Connector? local = AnchorConnectors(anchor).OrderBy(c => c.Origin.DistanceTo(external.Origin)).FirstOrDefault();
        if (local == null || SafeIsConnectedTo(local, external)) return;
        ConnectAnchor(anchor, external);
    }

    private static bool CanDirectConnect(Connector a, Connector b)
    {
        try
        {
            if (a.Origin.DistanceTo(b.Origin) > ConnectorCoincidenceTolerance) return false;
            XYZ da = FlexConnectorUtil.GetDirection(a);
            XYZ db = FlexConnectorUtil.GetDirection(b);
            if (da.GetLength() < 1e-8 || db.GetLength() < 1e-8) return true;
            return Math.Abs(da.Normalize().DotProduct(db.Normalize())) > 0.92;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsConnectorOccupied(Connector connector)
    {
        try { return connector.IsConnected; }
        catch { return true; }
    }

    private static bool SafeIsConnectedTo(Connector a, Connector b)
    {
        try { return a.IsConnectedTo(b); }
        catch { return false; }
    }

    private static bool IsConnectedToAnchor(Connector external, Conduit anchor)
    {
        try
        {
            foreach (Connector reference in external.AllRefs.Cast<Connector>())
                if (reference.Owner?.Id == anchor.Id) return true;
        }
        catch { }
        return false;
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

        foreach (Connector r in connector.AllRefs.Cast<Connector>())
            if (r.Owner is Conduit c) return c;

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
