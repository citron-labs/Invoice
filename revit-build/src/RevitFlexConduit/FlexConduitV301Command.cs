using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitOperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace RevitFlexConduit;

/// <summary>
/// Native-style Flex Conduit creation entry point.
///
/// Workflow:
/// 1. Click START. A click on an open conduit/MEP connector binds automatically;
///    otherwise the picked XYZ is a free endpoint.
/// 2. Click intermediate XYZ control points.
/// 3. Click another open connector to finish immediately, or press ENTER to
///    finish at the last clicked free point. ESC cancels the current run.
///
/// When an endpoint is attached to native conduit, the Flex run inherits the
/// attached conduit type, diameter, level, service/system information and
/// related settings. START takes precedence when both ends are conduits.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class FlexConduitV301Command : IExternalCommand
{
    private const double ConnectorHitTolerance = 0.012; // ft, ~1/8 in
    private const double ConnectorSearchRadius = 0.15;  // ft, bounding-box search only

    private static ObjectSnapTypes Snaps => ObjectSnapTypes.Endpoints |
                                                  ObjectSnapTypes.Midpoints |
                                                  ObjectSnapTypes.Intersections |
                                                  ObjectSnapTypes.Nearest |
                                                  ObjectSnapTypes.Points;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiapp = commandData.Application;
        UIDocument uidoc = uiapp.ActiveUIDocument;
        try
        {
            if (FlexV3Controller.TryGetSelectedRecord(uidoc, out _, out _))
                return FlexV3Controller.CreateOrEdit(commandData, ref message);

            EnsureParameters(uidoc.Document);
            return CreateNew(uiapp);
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

    private static Result CreateNew(UIApplication uiapp)
    {
        UIDocument uidoc = uiapp.ActiveUIDocument;
        Document doc = uidoc.Document;

        // A preselected conduit can still act as a template, but a conduit
        // actually attached to START/END takes precedence for inheritance.
        Conduit? selectedTemplate = uidoc.Selection.GetElementIds()
            .Select(doc.GetElement)
            .OfType<Conduit>()
            .FirstOrDefault();
        FlexV3Settings settings = FlexV3Engine.CaptureSettings(doc, selectedTemplate, uidoc.ActiveView);

        FlexEndpointPick start = PickEndpoint(uidoc, "START");
        Conduit? startSource = FindSourceConduit(doc, start.Binding);
        if (startSource != null)
            settings = FlexV3Engine.CaptureSettings(doc, startSource, uidoc.ActiveView);

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
        IntPtr mainWindow = uiapp.MainWindowHandle != IntPtr.Zero
            ? uiapp.MainWindowHandle
            : Process.GetCurrentProcess().MainWindowHandle;

        using var enterHook = new EnterToFinishHook(mainWindow, () => previewExists);

        try
        {
            while (true)
            {
                XYZ picked = uidoc.Selection.PickPoint(
                    Snaps,
                    "Flex Conduit: click control points. Click an open conduit/electrical connector to FINISH. Press ENTER to finish at the last point. ESC cancels.");

                // Clicking a second valid connector is the native-style finish action.
                if (TryResolveConnectorAtPoint(uidoc, picked, out Element? owner, out Connector? connector) &&
                    owner != null && connector != null &&
                    !IsSameAsStart(record, owner, connector))
                {
                    var end = new FlexEndpointPick
                    {
                        Point = connector.Origin,
                        Binding = FlexConnectorUtil.CreateBinding(owner, connector),
                        Owner = owner
                    };

                    // START-attached conduit wins. If START was free/non-conduit,
                    // inherit diameter/type/service from the attached END conduit.
                    if (startSource == null)
                    {
                        Conduit? endSource = FindSourceConduit(doc, end.Binding);
                        if (endSource != null)
                            record.Settings = FlexV3Engine.CaptureSettings(doc, endSource, uidoc.ActiveView);
                    }

                    FinalizeRun(uidoc, record, end);
                    return Result.Succeeded;
                }

                if (record.XyzPoints.Any(x => x.DistanceTo(picked) < FlexV3Engine.MinSpacing))
                    continue;

                record.Points.Add(new FlexPointDto(picked));
                record.End = FlexConnectorBinding.Disconnected(picked);
                if (record.Points.Count >= 2)
                {
                    UpdateRun(doc, record, uidoc.ActiveView);
                    previewExists = true;
                }
            }
        }
        catch (RevitOperationCanceledException)
        {
            // ENTER is translated to an internal ESC only to release Revit's
            // blocking PickPoint call. The run then finishes at its last point.
            if (enterHook.ConsumeEnterRequest() && previewExists)
            {
                XYZ last = record.XyzPoints[^1];
                var end = new FlexEndpointPick
                {
                    Point = last,
                    Binding = FlexConnectorBinding.Disconnected(last)
                };
                FinalizeRun(uidoc, record, end);
                return Result.Succeeded;
            }

            // A normal ESC means cancel, matching normal Revit command behavior.
            if (previewExists) DeleteRun(doc, runId);
            return Result.Cancelled;
        }
    }

    private static void FinalizeRun(UIDocument uidoc, FlexV3Record record, FlexEndpointPick end)
    {
        Document doc = uidoc.Document;
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
        SelectBody(uidoc, record.RunId);
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

    private static bool IsSameAsStart(FlexV3Record record, Element owner, Connector connector)
    {
        if (!record.Start.Connected) return false;
        FlexConnectorBinding candidate = FlexConnectorUtil.CreateBinding(owner, connector);
        if (candidate.OwnerId != record.Start.OwnerId) return false;
        if (candidate.ConnectorIndex >= 0 && record.Start.ConnectorIndex >= 0)
            return candidate.ConnectorIndex == record.Start.ConnectorIndex;
        return candidate.Origin.ToXyz().DistanceTo(record.Start.Origin.ToXyz()) < ConnectorHitTolerance;
    }

    private static Conduit? FindSourceConduit(Document doc, FlexConnectorBinding binding)
    {
        if (!binding.Connected || !FlexConnectorUtil.TryResolve(doc, binding, out Element? owner, out Connector? connector) || owner == null || connector == null)
            return null;

        if (owner is Conduit direct)
            return direct;

        // If the picked endpoint belongs to a fitting/equipment connector, use
        // any native conduit feeding that element as the inheritance source.
        try
        {
            foreach (Connector c in FlexConnectorUtil.GetConnectors(owner))
            {
                foreach (Connector r in c.AllRefs.Cast<Connector>())
                {
                    if (r.Owner is Conduit connected)
                        return connected;
                }
            }
        }
        catch { }

        return null;
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
        FlexNativeEndpointService.DeleteAnchors(doc, runId);
        tx.Commit();
    }

    private static void SelectBody(UIDocument uidoc, string runId)
    {
        DirectShape? body = FlexV3Data.FindBody(uidoc.Document, runId);
        if (body != null)
            uidoc.Selection.SetElementIds(new[] { body.Id });
    }

    private sealed record ConnectorHit(Element Owner, Connector Connector, double Distance, int Priority);

    /// <summary>
    /// Revit Selection.PickPoint is blocking and the public API does not expose
    /// an "accept points with Enter" callback. This temporary keyboard hook only
    /// exists while this Flex command is active. ENTER is consumed and a single
    /// ESC message is posted to Revit to release PickPoint; the command recognizes
    /// that synthetic cancel and commits the last clicked point as the endpoint.
    /// </summary>
    private sealed class EnterToFinishHook : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private const int WmKeyUp = 0x0101;
        private const int VkReturn = 0x0D;
        private const int VkEscape = 0x1B;

        private readonly IntPtr _mainWindow;
        private readonly Func<bool> _canFinish;
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hook;
        private volatile bool _enterRequested;

        internal EnterToFinishHook(IntPtr mainWindow, Func<bool> canFinish)
        {
            _mainWindow = mainWindow;
            _canFinish = canFinish;
            _proc = HookCallback;
            try
            {
                _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
            }
            catch
            {
                _hook = IntPtr.Zero;
            }
        }

        internal bool ConsumeEnterRequest()
        {
            bool value = _enterRequested;
            _enterRequested = false;
            return value;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown))
            {
                KbdLlHookStruct data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if (data.vkCode == VkReturn && _canFinish())
                {
                    _enterRequested = true;
                    if (_mainWindow != IntPtr.Zero)
                    {
                        PostMessage(_mainWindow, WmKeyDown, (IntPtr)VkEscape, IntPtr.Zero);
                        PostMessage(_mainWindow, WmKeyUp, (IntPtr)VkEscape, IntPtr.Zero);
                    }
                    return (IntPtr)1; // consume ENTER so Revit does not also act on it
                }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hook); } catch { }
                _hook = IntPtr.Zero;
            }
            GC.KeepAlive(_proc);
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
