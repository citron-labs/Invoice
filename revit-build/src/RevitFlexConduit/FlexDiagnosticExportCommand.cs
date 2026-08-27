using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace RevitFlexConduit;

/// <summary>
/// Exports a human-readable diagnostic snapshot for Flex Conduit support.
/// The report is read-only: it never changes model geometry or connections.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class FlexDiagnosticExportCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        try
        {
            string report = BuildReport(commandData, uidoc);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string safeTitle = SanitizeFileName(string.IsNullOrWhiteSpace(doc.Title) ? "RevitModel" : doc.Title);

            var dialog = new SaveFileDialog
            {
                Title = "Export Flex Conduit Diagnostic Log",
                Filter = "Text log (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = $"FlexConduit-Diagnostic-{safeTitle}-{stamp}.txt"
            };

            bool? accepted = dialog.ShowDialog();
            if (accepted != true || string.IsNullOrWhiteSpace(dialog.FileName))
                return Result.Cancelled;

            File.WriteAllText(dialog.FileName, report, new UTF8Encoding(false));
            TaskDialog.Show(
                $"Flex Conduit v{App301.ProductVersion}",
                "Diagnostic log exported successfully.\n\n" + dialog.FileName +
                "\n\nUpload this .txt file when reporting a Flex Conduit problem.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.ToString();
            TaskDialog.Show(
                $"Flex Conduit v{App301.ProductVersion}",
                "The diagnostic log could not be exported.\n\n" + ex.Message);
            return Result.Failed;
        }
    }

    private static string BuildReport(ExternalCommandData commandData, UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        Autodesk.Revit.ApplicationServices.Application app = commandData.Application.Application;
        Assembly assembly = Assembly.GetExecutingAssembly();
        var sb = new StringBuilder(32_768);

        sb.AppendLine("REVIT FLEX CONDUIT 2025 - DIAGNOSTIC LOG");
        sb.AppendLine(new string('=', 72));
        sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Plugin reported version: {App301.ProductVersion}");
        sb.AppendLine($"DLL assembly version: {assembly.GetName().Version}");
        sb.AppendLine($"DLL location: {assembly.Location}");
        sb.AppendLine($"Revit version: {app.VersionName} ({app.VersionNumber})");
        sb.AppendLine($"Document title: {doc.Title}");
        sb.AppendLine($"Document path: {(string.IsNullOrWhiteSpace(doc.PathName) ? "<unsaved>" : doc.PathName)}");
        sb.AppendLine($"Workshared: {doc.IsWorkshared}");
        sb.AppendLine($"Active view: {uidoc.ActiveView.Name} | Id {uidoc.ActiveView.Id.Value} | {uidoc.ActiveView.ViewType}");
        sb.AppendLine($"Selected element IDs: {string.Join(", ", uidoc.Selection.GetElementIds().Select(id => id.Value))}");
        sb.AppendLine();

        List<DirectShape> bodies = FlexV3Data.AllBodies(doc).ToList();
        int conduitCount = new FilteredElementCollector(doc).OfClass(typeof(Conduit)).GetElementCount();
        int conduitTypeCount = new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).GetElementCount();
        int levelCount = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();

        sb.AppendLine("MODEL SUMMARY");
        sb.AppendLine(new string('-', 72));
        sb.AppendLine($"Flex Conduit runs: {bodies.Count}");
        sb.AppendLine($"Native conduits: {conduitCount}");
        sb.AppendLine($"Conduit types: {conduitTypeCount}");
        sb.AppendLine($"Levels: {levelCount}");
        sb.AppendLine($"Updater registered: {FlexConduitV3Updater.IsRegistered}");
        sb.AppendLine($"Updater executing now: {FlexConduitV3Updater.IsExecuting}");
        Check(sb, conduitTypeCount > 0, "At least one native Conduit Type exists.", "No Conduit Type exists in the project.");
        Check(sb, levelCount > 0, "At least one Level exists.", "No Level exists; native endpoint anchors cannot be created.");
        sb.AppendLine();

        if (bodies.Count == 0)
        {
            sb.AppendLine("No Flex Conduit runs were found in this document.");
            return sb.ToString();
        }

        HashSet<long> selected = uidoc.Selection.GetElementIds().Select(id => id.Value).ToHashSet();
        List<DirectShape> ordered = bodies
            .OrderByDescending(b => selected.Contains(b.Id.Value))
            .ThenBy(b => b.Id.Value)
            .ToList();

        int runNumber = 0;
        foreach (DirectShape body in ordered)
        {
            runNumber++;
            sb.AppendLine();
            sb.AppendLine($"RUN {runNumber} {(selected.Contains(body.Id.Value) ? "[SELECTED]" : string.Empty)}");
            sb.AppendLine(new string('-', 72));

            if (!FlexV3Data.TryRead(body, out FlexV3Record record))
            {
                sb.AppendLine($"FAIL  Body Id {body.Id.Value}: Flex Extensible Storage could not be read.");
                continue;
            }

            WriteRun(sb, doc, uidoc.ActiveView, body, record);
        }

        sb.AppendLine();
        sb.AppendLine(new string('=', 72));
        sb.AppendLine("END OF DIAGNOSTIC LOG");
        return sb.ToString();
    }

    private static void WriteRun(StringBuilder sb, Document doc, View view, DirectShape body, FlexV3Record record)
    {
        List<XYZ> points = record.XyzPoints;
        List<DirectShape> markers = FlexV3Data.FindMarkers(doc, record.RunId);
        Conduit? startAnchor = FlexNativeEndpointService.FindAnchor(doc, record.RunId, FlexNativeEndpointService.StartKind);
        Conduit? endAnchor = FlexNativeEndpointService.FindAnchor(doc, record.RunId, FlexNativeEndpointService.EndKind);

        sb.AppendLine($"Run ID: {record.RunId}");
        sb.AppendLine($"Body element Id: {body.Id.Value}");
        sb.AppendLine($"Body category: {body.Category?.Name ?? "<none>"}");
        sb.AppendLine($"Record version/kind: {record.Version} / {record.Kind}");
        sb.AppendLine($"Control points: {points.Count}");
        sb.AppendLine($"Persistent markers: {markers.Count}");
        sb.AppendLine($"Stored spline length: {FormatLength(record.SplineLength)}");
        sb.AppendLine($"Diameter: {FormatDiameter(record.Settings.Diameter)}");
        sb.AppendLine($"Conduit type: {record.Settings.TypeName} | TypeId {record.Settings.TypeId}");
        sb.AppendLine($"Service Type: {record.Settings.ServiceType}");
        sb.AppendLine($"System: {record.Settings.SystemName}");
        sb.AppendLine($"Reference Level: {record.Settings.LevelName} | LevelId {record.Settings.LevelId}");
        sb.AppendLine($"Material: {record.Settings.Material}");
        sb.AppendLine($"Workset: {record.Settings.Workset}");
        sb.AppendLine($"Phase: {record.Settings.Phase}");
        sb.AppendLine($"Design Option: {record.Settings.DesignOption}");

        Check(sb, points.Count >= 2,
            "Run has at least two XYZ control points.",
            "Run has fewer than two control points.");
        Check(sb, markers.Count == points.Count,
            "Persistent marker count matches control-point count.",
            $"Marker count ({markers.Count}) does not match control-point count ({points.Count}).");
        Check(sb, record.Settings.Diameter > 1e-6,
            "Flex diameter is valid.",
            "Flex diameter is zero/invalid.");
        Check(sb, doc.GetElement(new ElementId(record.Settings.TypeId)) is ConduitType,
            "Stored Conduit Type resolves in the project.",
            $"Stored Conduit Type Id {record.Settings.TypeId} does not resolve to a ConduitType.");

        try
        {
            FlexV3Record cloned = record.Clone();
            double recomputed = FlexV3Engine.BuildSpline(cloned, view).ApproximateLength;
            double delta = Math.Abs(recomputed - record.SplineLength);
            sb.AppendLine($"Recomputed spline length: {FormatLength(recomputed)} | delta {FormatLength(delta)}");
            Check(sb, delta <= 0.002,
                "Stored spline length matches recomputed centerline length.",
                "Stored spline length differs from recomputed spline length.");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"FAIL  Spline recomputation threw: {ex.GetType().Name}: {ex.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("CONTROL POINTS (Revit internal feet + millimeters)");
        for (int i = 0; i < points.Count; i++)
        {
            XYZ p = points[i];
            sb.AppendLine($"  P{i}: X={p.X:F6} ft  Y={p.Y:F6} ft  Z={p.Z:F6} ft | X={p.X * 304.8:F1} mm  Y={p.Y * 304.8:F1} mm  Z={p.Z * 304.8:F1} mm");
        }

        sb.AppendLine();
        WriteEndpoint(sb, doc, "START", record.Start, startAnchor);
        WriteEndpoint(sb, doc, "END", record.End, endAnchor);

        string parameterDiameter = ReadParameter(body, FlexV3ParameterService.PDiameter);
        string parameterLength = ReadParameter(body, FlexV3ParameterService.PLength);
        string parameterService = ReadParameter(body, FlexV3ParameterService.PService);
        sb.AppendLine();
        sb.AppendLine("BODY PARAMETERS");
        sb.AppendLine($"  Flex Conduit Diameter: {parameterDiameter}");
        sb.AppendLine($"  Flex Conduit Length: {parameterLength}");
        sb.AppendLine($"  Flex Conduit Service Type: {parameterService}");
    }

    private static void WriteEndpoint(StringBuilder sb, Document doc, string label, FlexConnectorBinding binding, Conduit? anchor)
    {
        sb.AppendLine($"{label} ENDPOINT");
        sb.AppendLine($"  Bound: {binding.Connected}");
        sb.AppendLine($"  Stored owner: {binding.OwnerName} | Id {binding.OwnerId} | UniqueId {binding.OwnerUniqueId}");
        sb.AppendLine($"  Stored connector index: {binding.ConnectorIndex}");
        sb.AppendLine($"  Stored origin: {FormatPoint(binding.Origin.ToXyz())}");
        sb.AppendLine($"  Stored direction: {FormatPoint(binding.Direction.ToXyz())}");

        if (!binding.Connected)
        {
            sb.AppendLine("  INFO  Free XYZ endpoint; no native connector binding is expected.");
            if (anchor != null)
                sb.AppendLine($"  WARN  Native anchor Id {anchor.Id.Value} exists even though endpoint is free.");
            return;
        }

        bool resolved = FlexConnectorUtil.TryResolve(doc, binding, out Element? owner, out Connector? connector) && owner != null && connector != null;
        CheckIndented(sb, resolved,
            "Connector binding resolves to a live Revit element/connector.",
            "Stored connector binding cannot be resolved.");

        if (resolved && owner != null && connector != null)
        {
            double drift = connector.Origin.DistanceTo(binding.Origin.ToXyz());
            sb.AppendLine($"  Live owner: {owner.Name} | Id {owner.Id.Value} | Category {owner.Category?.Name ?? "<none>"}");
            sb.AppendLine($"  Live connector origin: {FormatPoint(connector.Origin)} | stored/live drift {FormatLength(drift)}");
            sb.AppendLine($"  Live connector direction: {FormatPoint(FlexConnectorUtil.GetDirection(connector))}");
            sb.AppendLine($"  Live connector IsConnected: {SafeConnectorConnected(connector)}");

            if (owner is Conduit conduit)
            {
                double diameter = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble() ?? 0;
                sb.AppendLine($"  Attached native conduit diameter: {FormatDiameter(diameter)}");
                sb.AppendLine($"  Attached native conduit type: {doc.GetElement(conduit.GetTypeId())?.Name ?? "<unknown>"}");
                sb.AppendLine($"  Attached native conduit service: {conduit.LookupParameter("Service Type")?.AsString() ?? string.Empty}");
            }
        }

        CheckIndented(sb, anchor != null,
            "Native conduit endpoint anchor exists.",
            "No native endpoint anchor exists for this bound endpoint.");

        if (anchor != null)
        {
            double length = (anchor.Location as LocationCurve)?.Curve.Length ?? 0;
            double diameter = anchor.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble() ?? 0;
            List<Connector> connectors = anchor.ConnectorManager.Connectors.Cast<Connector>().ToList();
            int connectedCount = connectors.Count(SafeConnectorConnected);
            sb.AppendLine($"  Anchor Id: {anchor.Id.Value}");
            sb.AppendLine($"  Anchor length: {FormatLength(length)}");
            sb.AppendLine($"  Anchor diameter: {FormatDiameter(diameter)}");
            sb.AppendLine($"  Anchor connectors: {connectors.Count}; connected connectors: {connectedCount}");
        }
    }

    private static string ReadParameter(Element element, string name)
    {
        try
        {
            Parameter? p = element.LookupParameter(name);
            if (p == null) return "<parameter not bound>";
            return p.StorageType switch
            {
                StorageType.Double => $"{p.AsDouble():F6} internal | {p.AsValueString()}",
                StorageType.Integer => p.AsInteger().ToString(CultureInfo.InvariantCulture),
                StorageType.ElementId => p.AsElementId().Value.ToString(CultureInfo.InvariantCulture),
                StorageType.String => p.AsString() ?? string.Empty,
                _ => p.AsValueString() ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            return $"<read failed: {ex.Message}>";
        }
    }

    private static bool SafeConnectorConnected(Connector connector)
    {
        try { return connector.IsConnected; }
        catch { return false; }
    }

    private static void Check(StringBuilder sb, bool ok, string success, string failure)
        => sb.AppendLine(ok ? $"PASS  {success}" : $"FAIL  {failure}");

    private static void CheckIndented(StringBuilder sb, bool ok, string success, string failure)
        => sb.AppendLine(ok ? $"  PASS  {success}" : $"  WARN  {failure}");

    private static string FormatPoint(XYZ p) => $"({p.X:F6}, {p.Y:F6}, {p.Z:F6}) ft";

    private static string FormatLength(double feet)
        => $"{feet:F6} ft | {feet * 12.0:F3} in | {feet * 304.8:F1} mm";

    private static string FormatDiameter(double feet)
        => $"{feet * 12.0:F4} in | {feet * 304.8:F2} mm";

    private static string SanitizeFileName(string value)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }
}
