using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RevitFlexConduit;

internal sealed class FlexPointDto
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public FlexPointDto() { }
    public FlexPointDto(XYZ p) { X = p.X; Y = p.Y; Z = p.Z; }
    public XYZ ToXyz() => new(X, Y, Z);
}

internal sealed class FlexConnectorBinding
{
    public bool Connected { get; set; }
    public string OwnerUniqueId { get; set; } = string.Empty;
    public long OwnerId { get; set; } = -1;
    public string OwnerName { get; set; } = string.Empty;
    public int ConnectorIndex { get; set; } = -1;
    public FlexPointDto Origin { get; set; } = new();
    public FlexPointDto Direction { get; set; } = new() { X = 1 };

    public static FlexConnectorBinding Disconnected(XYZ origin) => new()
    {
        Connected = false,
        Origin = new FlexPointDto(origin),
        Direction = new FlexPointDto(XYZ.BasisX)
    };
}

internal sealed class FlexV3Settings
{
    public long TypeId { get; set; } = -1;
    public string TypeName { get; set; } = string.Empty;
    public long LevelId { get; set; } = -1;
    public string LevelName { get; set; } = string.Empty;
    public double Diameter { get; set; } = 1.0 / 12.0;
    public string ServiceType { get; set; } = string.Empty;
    public string SystemName { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Mark { get; set; } = string.Empty;
    public string Material { get; set; } = string.Empty;
    public string Workset { get; set; } = string.Empty;
    public long CreatedPhaseId { get; set; } = -1;
    public string Phase { get; set; } = string.Empty;
    public string DesignOption { get; set; } = string.Empty;
}

internal sealed class FlexV3Record
{
    public int Version { get; set; } = 3;
    public string RunId { get; set; } = string.Empty;
    public string Kind { get; set; } = "Body";
    public int MarkerIndex { get; set; } = -1;
    public List<FlexPointDto> Points { get; set; } = new();
    public FlexConnectorBinding Start { get; set; } = new();
    public FlexConnectorBinding End { get; set; } = new();
    public FlexV3Settings Settings { get; set; } = new();
    public double SplineLength { get; set; }

    public List<XYZ> XyzPoints => Points.Select(p => p.ToXyz()).ToList();

    public FlexV3Record Clone()
    {
        string json = JsonSerializer.Serialize(this, FlexV3Data.JsonOptions);
        return JsonSerializer.Deserialize<FlexV3Record>(json, FlexV3Data.JsonOptions) ?? new FlexV3Record();
    }
}

internal static class FlexV3Data
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly Guid SchemaGuid = new("F63F83E0-4EC8-4D2D-91EF-A2A20D58D331");

    private static Schema GetOrCreateSchema()
    {
        Schema? existing = Schema.Lookup(SchemaGuid);
        if (existing != null) return existing;

        var builder = new SchemaBuilder(SchemaGuid);
        builder.SetSchemaName("RevitFlexConduitV3");
        builder.SetVendorId("CTRN");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("Payload", typeof(string));
        return builder.Finish();
    }

    internal static void Write(Element element, FlexV3Record record)
    {
        Schema schema = GetOrCreateSchema();
        var entity = new Entity(schema);
        entity.Set(schema.GetField("Payload"), JsonSerializer.Serialize(record, JsonOptions));
        element.SetEntity(entity);
    }

    internal static bool TryRead(Element? element, out FlexV3Record record)
    {
        record = new FlexV3Record();
        if (element == null) return false;

        Schema? schema = Schema.Lookup(SchemaGuid);
        if (schema == null) return false;

        Entity entity = element.GetEntity(schema);
        if (!entity.IsValid()) return false;

        try
        {
            string json = entity.Get<string>(schema.GetField("Payload")) ?? string.Empty;
            FlexV3Record? parsed = JsonSerializer.Deserialize<FlexV3Record>(json, JsonOptions);
            if (parsed == null || parsed.Version < 3 || string.IsNullOrWhiteSpace(parsed.RunId)) return false;
            record = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static DirectShape? FindBody(Document doc, string runId)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .Cast<DirectShape>()
            .FirstOrDefault(e => TryRead(e, out FlexV3Record r) && r.RunId == runId && r.Kind == "Body");

    internal static List<DirectShape> FindMarkers(Document doc, string runId)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .Cast<DirectShape>()
            .Where(e => TryRead(e, out FlexV3Record r) && r.RunId == runId && r.Kind == "Marker")
            .OrderBy(e => TryRead(e, out FlexV3Record r) ? r.MarkerIndex : int.MaxValue)
            .ToList();

    internal static IEnumerable<DirectShape> AllBodies(Document doc)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .Cast<DirectShape>()
            .Where(e => TryRead(e, out FlexV3Record r) && r.Kind == "Body");
}

internal static class FlexConnectorUtil
{
    internal static List<Connector> GetConnectors(Element element)
    {
        try
        {
            if (element is MEPCurve curve)
                return curve.ConnectorManager.Connectors.Cast<Connector>().ToList();

            if (element is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
                return fi.MEPModel.ConnectorManager.Connectors.Cast<Connector>().ToList();
        }
        catch { }

        return new List<Connector>();
    }

    internal static Connector? FindNearest(Element owner, XYZ near)
        => GetConnectors(owner)
            .OrderBy(c => c.Origin.DistanceTo(near))
            .FirstOrDefault();

    internal static FlexConnectorBinding CreateBinding(Element owner, Connector connector)
    {
        List<Connector> all = GetConnectors(owner);
        int index = all.FindIndex(c => ReferenceEquals(c, connector) || c.Origin.IsAlmostEqualTo(connector.Origin));
        XYZ direction = GetDirection(connector);
        return new FlexConnectorBinding
        {
            Connected = true,
            OwnerUniqueId = owner.UniqueId,
            OwnerId = owner.Id.Value,
            OwnerName = owner.Name ?? string.Empty,
            ConnectorIndex = index,
            Origin = new FlexPointDto(connector.Origin),
            Direction = new FlexPointDto(direction)
        };
    }

    internal static bool TryResolve(Document doc, FlexConnectorBinding binding, out Element? owner, out Connector? connector)
    {
        owner = null;
        connector = null;
        if (!binding.Connected) return false;

        try
        {
            if (!string.IsNullOrWhiteSpace(binding.OwnerUniqueId))
                owner = doc.GetElement(binding.OwnerUniqueId);
        }
        catch { owner = null; }

        if (owner == null && binding.OwnerId >= 0)
            owner = doc.GetElement(new ElementId(binding.OwnerId));
        if (owner == null) return false;

        List<Connector> connectors = GetConnectors(owner);
        if (binding.ConnectorIndex >= 0 && binding.ConnectorIndex < connectors.Count)
        {
            connector = connectors[binding.ConnectorIndex];
            return true;
        }

        connector = connectors.OrderBy(c => c.Origin.DistanceTo(binding.Origin.ToXyz())).FirstOrDefault();
        return connector != null;
    }

    internal static XYZ GetDirection(Connector connector)
    {
        try
        {
            XYZ z = connector.CoordinateSystem.BasisZ;
            if (z.GetLength() > 1e-9) return z.Normalize();
        }
        catch { }
        return XYZ.BasisX;
    }
}

internal sealed class FlexConnectorOwnerFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) => FlexConnectorUtil.GetConnectors(elem).Count > 0;
    public bool AllowReference(Reference reference, XYZ position) => false;
}

internal static class FlexV3ParameterService
{
    internal const string PRunId = "Flex Conduit Run ID";
    internal const string PLength = "Flex Conduit Length";
    internal const string PDiameter = "Flex Conduit Diameter";
    internal const string PType = "Flex Conduit Type";
    internal const string PService = "Flex Conduit Service Type";
    internal const string PSystem = "Flex Conduit System";
    internal const string PStartEquipment = "Flex Conduit Start Equipment";
    internal const string PEndEquipment = "Flex Conduit End Equipment";
    internal const string PStartElevation = "Flex Conduit Start Elevation";
    internal const string PEndElevation = "Flex Conduit End Elevation";
    internal const string PMaterial = "Flex Conduit Material";
    internal const string PLevel = "Flex Conduit Reference Level";
    internal const string PWorkset = "Flex Conduit Workset";
    internal const string PPhase = "Flex Conduit Phase";
    internal const string PDesignOption = "Flex Conduit Design Option";

    private sealed record ParamDef(string Name, Guid Guid, ForgeTypeId Type);

    private static readonly ParamDef[] Definitions =
    {
        new(PRunId, new Guid("E439A3DD-0860-40C2-AB9D-E7BFEA22C861"), SpecTypeId.String.Text),
        new(PLength, new Guid("D21C3BCD-7ADF-4709-9F22-1DCE8CF8DB95"), SpecTypeId.Length),
        new(PDiameter, new Guid("ED0A9FB7-23B5-438D-B647-B0E2924D7FB2"), SpecTypeId.Length),
        new(PType, new Guid("078D7F6F-4B25-4C3B-87D1-D9E51F654146"), SpecTypeId.String.Text),
        new(PService, new Guid("CF86D399-7663-47ED-9C06-3DB0C83AA2F0"), SpecTypeId.String.Text),
        new(PSystem, new Guid("B6994CFD-D905-43D0-BA53-41392C90831F"), SpecTypeId.String.Text),
        new(PStartEquipment, new Guid("68A19654-CA05-4A2C-A6A0-BC4E818CC63F"), SpecTypeId.String.Text),
        new(PEndEquipment, new Guid("384B1EE4-10C4-46EC-B74C-A8C787AA0F45"), SpecTypeId.String.Text),
        new(PStartElevation, new Guid("3A5BA9CA-265E-46DE-9DBC-D144621C1221"), SpecTypeId.Length),
        new(PEndElevation, new Guid("07D4F3FB-C565-41D7-8B3E-D4C42C7A7224"), SpecTypeId.Length),
        new(PMaterial, new Guid("2B8258A7-E1E2-4BBB-991C-CBC7886FC9BD"), SpecTypeId.String.Text),
        new(PLevel, new Guid("69AA6FF2-CB46-41AB-951F-F4CC5856B6AD"), SpecTypeId.String.Text),
        new(PWorkset, new Guid("A0B74043-6B0C-4F84-9190-773E37AB0BE4"), SpecTypeId.String.Text),
        new(PPhase, new Guid("61B68922-D8C4-4689-A782-F5503275B4E5"), SpecTypeId.String.Text),
        new(PDesignOption, new Guid("46D056C4-E851-4530-B42A-374B84FD2AE3"), SpecTypeId.String.Text),
    };

    internal static void Ensure(Document doc)
    {
        if (doc.IsFamilyDocument || doc.IsReadOnly) return;
        if (Definitions.All(d => HasDefinition(doc, d.Name))) return;

        string original = doc.Application.SharedParametersFilename ?? string.Empty;
        string temp = Path.Combine(Path.GetTempPath(), "RevitFlexConduit-v3-shared-parameters.txt");

        try
        {
            File.WriteAllText(temp,
                "# This is a Revit shared parameter file.\r\n" +
                "# Do not edit manually.\r\n" +
                "*META\tVERSION\tMINVERSION\r\n" +
                "META\t2\t1\r\n" +
                "*GROUP\tID\tNAME\r\n" +
                "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n",
                new UTF8Encoding(false));

            doc.Application.SharedParametersFilename = temp;
            DefinitionFile? file = doc.Application.OpenSharedParameterFile();
            if (file == null) return;

            DefinitionGroup group = file.Groups.get_Item("Flex Conduit") ?? file.Groups.Create("Flex Conduit");
            CategorySet categories = doc.Application.Create.NewCategorySet();
            Category? conduit = Category.GetCategory(doc, BuiltInCategory.OST_Conduit);
            Category? generic = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
            if (conduit != null && conduit.AllowsBoundParameters) categories.Insert(conduit);
            if (generic != null && generic.AllowsBoundParameters) categories.Insert(generic);
            if (categories.IsEmpty) return;

            InstanceBinding binding = doc.Application.Create.NewInstanceBinding(categories);
            foreach (ParamDef p in Definitions)
            {
                if (HasDefinition(doc, p.Name)) continue;
                using var options = new ExternalDefinitionCreationOptions(p.Name, p.Type)
                {
                    GUID = p.Guid,
                    Description = "Revit Flex Conduit 2025 v3 parameter",
                    UserModifiable = true,
                    Visible = true
                };
                Definition definition = group.Definitions.Create(options);
                doc.ParameterBindings.Insert(definition, binding, GroupTypeId.Electrical);
            }
        }
        catch
        {
            // Flex geometry remains usable even in projects where project-parameter binding is restricted.
        }
        finally
        {
            try { doc.Application.SharedParametersFilename = original; } catch { }
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static bool HasDefinition(Document doc, string name)
    {
        DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
        it.Reset();
        while (it.MoveNext())
        {
            if (string.Equals(it.Key?.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static void Apply(Element body, FlexV3Record record)
    {
        List<XYZ> pts = record.XyzPoints;
        if (pts.Count < 2) return;

        SetString(body, PRunId, record.RunId);
        SetDouble(body, PLength, record.SplineLength);
        SetDouble(body, PDiameter, record.Settings.Diameter);
        SetString(body, PType, record.Settings.TypeName);
        SetString(body, PService, record.Settings.ServiceType);
        SetString(body, PSystem, record.Settings.SystemName);
        SetString(body, PStartEquipment, record.Start.Connected ? record.Start.OwnerName : string.Empty);
        SetString(body, PEndEquipment, record.End.Connected ? record.End.OwnerName : string.Empty);
        SetDouble(body, PStartElevation, pts[0].Z);
        SetDouble(body, PEndElevation, pts[^1].Z);
        SetString(body, PMaterial, record.Settings.Material);
        SetString(body, PLevel, record.Settings.LevelName);
        SetString(body, PWorkset, record.Settings.Workset);
        SetString(body, PPhase, record.Settings.Phase);
        SetString(body, PDesignOption, record.Settings.DesignOption);

        Parameter? comments = body.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments != null && !comments.IsReadOnly && comments.StorageType == StorageType.String)
            comments.Set(record.Settings.Comments ?? string.Empty);
        Parameter? mark = body.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
        if (mark != null && !mark.IsReadOnly && mark.StorageType == StorageType.String)
            mark.Set(record.Settings.Mark ?? string.Empty);
    }

    internal static bool TryReadEditableProperties(Element body, FlexV3Record record, out bool geometryChanged)
    {
        geometryChanged = false;
        Parameter? d = body.LookupParameter(PDiameter);
        if (d != null && d.StorageType == StorageType.Double)
        {
            double value = d.AsDouble();
            if (value > 1e-6 && Math.Abs(value - record.Settings.Diameter) > 1e-8)
            {
                record.Settings.Diameter = value;
                geometryChanged = true;
            }
        }

        record.Settings.ServiceType = ReadString(body, PService, record.Settings.ServiceType);
        record.Settings.SystemName = ReadString(body, PSystem, record.Settings.SystemName);
        record.Settings.Material = ReadString(body, PMaterial, record.Settings.Material);
        Parameter? comments = body.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments?.StorageType == StorageType.String) record.Settings.Comments = comments.AsString() ?? record.Settings.Comments;
        Parameter? mark = body.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
        if (mark?.StorageType == StorageType.String) record.Settings.Mark = mark.AsString() ?? record.Settings.Mark;
        return true;
    }

    private static string ReadString(Element e, string name, string fallback)
    {
        Parameter? p = e.LookupParameter(name);
        return p?.StorageType == StorageType.String ? p.AsString() ?? fallback : fallback;
    }

    private static void SetString(Element e, string name, string value)
    {
        Parameter? p = e.LookupParameter(name);
        if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value ?? string.Empty);
    }

    private static void SetDouble(Element e, string name, double value)
    {
        Parameter? p = e.LookupParameter(name);
        if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double) p.Set(value);
    }
}

internal static class FlexV3Engine
{
    internal const double DefaultDiameter = 1.0 / 12.0;
    internal const double MinSpacing = 0.01;
    private const double MarkerHalfSize = 0.12;

    internal static FlexV3Settings CaptureSettings(Document doc, Conduit? template, View view)
    {
        ElementId typeId = template?.GetTypeId()
            ?? new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<ConduitType>().FirstOrDefault()?.Id
            ?? ElementId.InvalidElementId;
        ElementId levelId = template?.ReferenceLevel?.Id
            ?? view.GenLevel?.Id
            ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElementId();

        double diameter = template?.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble() ?? DefaultDiameter;
        if (diameter <= 1e-6) diameter = DefaultDiameter;

        string typeName = doc.GetElement(typeId)?.Name ?? string.Empty;
        string levelName = doc.GetElement(levelId)?.Name ?? string.Empty;
        string service = template?.LookupParameter("Service Type")?.AsString() ?? string.Empty;
        string system = template?.LookupParameter("System Name")?.AsString() ?? string.Empty;
        string comments = template?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
        string mark = template?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty;
        string material = template?.LookupParameter("Material")?.AsValueString() ?? string.Empty;
        string workset = template?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)?.AsValueString() ?? string.Empty;
        long phaseId = template?.CreatedPhaseId.Value ?? -1;
        string phase = phaseId >= 0 ? doc.GetElement(new ElementId(phaseId))?.Name ?? string.Empty : string.Empty;
        string designOption = template?.DesignOption?.Name ?? string.Empty;

        return new FlexV3Settings
        {
            TypeId = typeId.Value,
            TypeName = typeName,
            LevelId = levelId.Value,
            LevelName = levelName,
            Diameter = diameter,
            ServiceType = service,
            SystemName = system,
            Comments = comments,
            Mark = mark,
            Material = material,
            Workset = workset,
            CreatedPhaseId = phaseId,
            Phase = phase,
            DesignOption = designOption
        };
    }

    internal static XYZ AutoMiddle(XYZ start, XYZ end, View view)
    {
        XYZ delta = end - start;
        double distance = delta.GetLength();
        XYZ mid = (start + end).Multiply(0.5);
        if (distance < MinSpacing) return mid;

        XYZ direction = delta.Normalize();
        XYZ guide = view.UpDirection;
        XYZ perpendicular = guide - direction.Multiply(guide.DotProduct(direction));
        if (perpendicular.GetLength() < 1e-6)
        {
            guide = view.RightDirection;
            perpendicular = guide - direction.Multiply(guide.DotProduct(direction));
        }
        if (perpendicular.GetLength() < 1e-6)
            perpendicular = direction.CrossProduct(XYZ.BasisZ);
        if (perpendicular.GetLength() < 1e-6)
            perpendicular = XYZ.BasisY;
        perpendicular = perpendicular.Normalize();
        double bow = Math.Clamp(distance * 0.08, 0.12, 0.9);
        return mid + perpendicular.Multiply(bow);
    }

    internal static HermiteSpline BuildSpline(FlexV3Record record, View? view)
    {
        List<XYZ> user = record.XyzPoints;
        if (user.Count < 2) throw new InvalidOperationException("Flex Conduit requires at least two points.");

        if (user.Count == 2 && view != null)
            user.Insert(1, AutoMiddle(user[0], user[1], view));

        var render = new List<XYZ> { user[0] };
        if (record.Start.Connected)
        {
            XYZ dir = record.Start.Direction.ToXyz();
            if (dir.GetLength() > 1e-8)
            {
                dir = dir.Normalize();
                XYZ toward = user[Math.Min(1, user.Count - 1)] - user[0];
                if (toward.GetLength() > 1e-8 && dir.DotProduct(toward.Normalize()) < 0) dir = -dir;
                double handle = Math.Clamp(user[0].DistanceTo(user[Math.Min(1, user.Count - 1)]) * 0.35, 0.15, 2.0);
                render.Add(user[0] + dir.Multiply(handle));
            }
        }

        for (int i = 1; i < user.Count - 1; i++) render.Add(user[i]);

        if (record.End.Connected)
        {
            XYZ dir = record.End.Direction.ToXyz();
            if (dir.GetLength() > 1e-8)
            {
                dir = dir.Normalize();
                XYZ fromPrev = user[^1] - user[^2];
                if (fromPrev.GetLength() > 1e-8 && dir.DotProduct(fromPrev.Normalize()) > 0) dir = -dir;
                double handle = Math.Clamp(user[^1].DistanceTo(user[^2]) * 0.35, 0.15, 2.0);
                render.Add(user[^1] + dir.Multiply(handle));
            }
        }

        render.Add(user[^1]);
        render = RemoveNearDuplicates(render);
        if (render.Count < 3 && view != null)
            render.Insert(1, AutoMiddle(render[0], render[^1], view));
        return HermiteSpline.Create(render, false);
    }

    private static List<XYZ> RemoveNearDuplicates(IEnumerable<XYZ> points)
    {
        var result = new List<XYZ>();
        foreach (XYZ p in points)
            if (result.Count == 0 || result[^1].DistanceTo(p) > 1e-5) result.Add(p);
        return result;
    }

    internal static void Regenerate(Document doc, FlexV3Record record, View? view, bool createMissingMarkers = true)
    {
        List<XYZ> points = record.XyzPoints;
        if (points.Count < 2) return;

        HermiteSpline spline = BuildSpline(record, view);
        record.SplineLength = spline.ApproximateLength;

        DirectShape? body = FlexV3Data.FindBody(doc, record.RunId);
        if (body == null)
        {
            ElementId conduitCategory = Category.GetCategory(doc, BuiltInCategory.OST_Conduit)?.Id ?? ElementId.InvalidElementId;
            ElementId genericCategory = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel)?.Id ?? ElementId.InvalidElementId;
            ElementId category = conduitCategory != ElementId.InvalidElementId && DirectShape.IsValidCategoryId(conduitCategory, doc)
                ? conduitCategory
                : genericCategory;
            body = DirectShape.CreateElement(doc, category);
            body.Name = $"Flex Conduit {record.RunId}";
        }

        body.SetShape(CreateBodyGeometry(spline, record.Settings.Diameter));
        FlexV3Data.Write(body, record);
        ApplyPhase(body, record.Settings);
        FlexV3ParameterService.Apply(body, record);

        List<DirectShape> markers = FlexV3Data.FindMarkers(doc, record.RunId);
        if (!createMissingMarkers && markers.Count < points.Count) return;

        ElementId markerCategory = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel)?.Id ?? ElementId.InvalidElementId;
        while (markers.Count < points.Count)
        {
            DirectShape marker = DirectShape.CreateElement(doc, markerCategory);
            markers.Add(marker);
        }
        while (markers.Count > points.Count)
        {
            DirectShape last = markers[^1];
            markers.RemoveAt(markers.Count - 1);
            doc.Delete(last.Id);
        }

        for (int i = 0; i < points.Count; i++)
        {
            DirectShape marker = markers[i];
            marker.Name = $"Flex Control {i + 1} [{record.RunId}]";
            marker.SetShape(MarkerGeometry(points[i]));
            FlexV3Record markerRecord = record.Clone();
            markerRecord.Kind = "Marker";
            markerRecord.MarkerIndex = i;
            FlexV3Data.Write(marker, markerRecord);
        }
    }

    private static void ApplyPhase(Element body, FlexV3Settings settings)
    {
        try
        {
            if (settings.CreatedPhaseId >= 0 && body.ArePhasesModifiable())
            {
                ElementId phase = new(settings.CreatedPhaseId);
                if (body.IsPhaseCreatedValid(phase)) body.CreatedPhaseId = phase;
            }
        }
        catch { }
    }

    private static IList<GeometryObject> CreateBodyGeometry(HermiteSpline spline, double diameter)
    {
        var geometry = new List<GeometryObject>();
        try
        {
            geometry.Add(CreateSplineTube(spline, Math.Max(diameter * 0.5, 0.01)));
        }
        catch
        {
            geometry.Add(spline);
        }
        return geometry;
    }

    private static Solid CreateSplineTube(Curve spline, double radius)
    {
        double startParameter = spline.GetEndParameter(0);
        Transform derivative = spline.ComputeDerivatives(startParameter, false);
        XYZ tangent = derivative.BasisX.Normalize();
        XYZ axisX = XYZ.BasisZ.CrossProduct(tangent);
        if (axisX.GetLength() < 1e-6) axisX = XYZ.BasisX.CrossProduct(tangent);
        if (axisX.GetLength() < 1e-6) axisX = XYZ.BasisY;
        axisX = axisX.Normalize();
        XYZ axisY = tangent.CrossProduct(axisX).Normalize();
        XYZ center = spline.GetEndPoint(0);

        var profile = new CurveLoop();
        profile.Append(Arc.Create(center, radius, 0, Math.PI, axisX, axisY));
        profile.Append(Arc.Create(center, radius, Math.PI, Math.PI * 2, axisX, axisY));
        var path = new CurveLoop();
        path.Append(spline);
        return GeometryCreationUtilities.CreateSweptGeometry(path, 0, startParameter, new List<CurveLoop> { profile });
    }

    private static IList<GeometryObject> MarkerGeometry(XYZ point)
    {
        double s = MarkerHalfSize;
        return new List<GeometryObject>
        {
            Line.CreateBound(point - XYZ.BasisX.Multiply(s), point + XYZ.BasisX.Multiply(s)),
            Line.CreateBound(point - XYZ.BasisY.Multiply(s), point + XYZ.BasisY.Multiply(s)),
            Line.CreateBound(point - XYZ.BasisZ.Multiply(s), point + XYZ.BasisZ.Multiply(s))
        };
    }

    internal static XYZ MarkerCenter(Element marker)
    {
        BoundingBoxXYZ? box = marker.get_BoundingBox(null);
        return box == null ? XYZ.Zero : (box.Min + box.Max).Multiply(0.5);
    }

    internal static void RefreshConnectorBinding(Document doc, FlexV3Record record)
    {
        List<XYZ> points = record.XyzPoints;
        if (points.Count < 2) return;

        if (FlexConnectorUtil.TryResolve(doc, record.Start, out Element? startOwner, out Connector? startConnector) && startConnector != null)
        {
            points[0] = startConnector.Origin;
            record.Start = FlexConnectorUtil.CreateBinding(startOwner!, startConnector);
        }
        if (FlexConnectorUtil.TryResolve(doc, record.End, out Element? endOwner, out Connector? endConnector) && endConnector != null)
        {
            points[^1] = endConnector.Origin;
            record.End = FlexConnectorUtil.CreateBinding(endOwner!, endConnector);
        }
        record.Points = points.Select(p => new FlexPointDto(p)).ToList();
    }

    internal static int FindInsertionIndex(List<XYZ> points, XYZ point)
    {
        if (points.Count < 2) return points.Count;
        double best = double.MaxValue;
        int bestIndex = 1;
        for (int i = 0; i < points.Count - 1; i++)
        {
            double d = DistanceToSegment(point, points[i], points[i + 1]);
            if (d < best) { best = d; bestIndex = i + 1; }
        }
        return bestIndex;
    }

    private static double DistanceToSegment(XYZ p, XYZ a, XYZ b)
    {
        XYZ ab = b - a;
        double len2 = ab.DotProduct(ab);
        if (len2 < 1e-12) return p.DistanceTo(a);
        double t = Math.Clamp((p - a).DotProduct(ab) / len2, 0, 1);
        return p.DistanceTo(a + ab.Multiply(t));
    }

    internal static void ApplyNativeConduitSettings(Conduit conduit, FlexV3Settings settings)
    {
        Parameter? diameter = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
        if (diameter != null && !diameter.IsReadOnly) diameter.Set(settings.Diameter);
        Parameter? service = conduit.LookupParameter("Service Type");
        if (service?.StorageType == StorageType.String && !service.IsReadOnly) service.Set(settings.ServiceType ?? string.Empty);
        Parameter? comments = conduit.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments?.StorageType == StorageType.String && !comments.IsReadOnly) comments.Set(settings.Comments ?? string.Empty);
        Parameter? mark = conduit.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
        if (mark?.StorageType == StorageType.String && !mark.IsReadOnly) mark.Set(settings.Mark ?? string.Empty);
    }

    internal static void RegisterRunTriggers(Document doc, FlexV3Record record)
    {
        DirectShape? body = FlexV3Data.FindBody(doc, record.RunId);
        List<ElementId> ids = new();
        if (body != null) ids.Add(body.Id);
        ids.AddRange(FlexV3Data.FindMarkers(doc, record.RunId).Select(m => m.Id));
        if (record.Start.Connected && record.Start.OwnerId >= 0) ids.Add(new ElementId(record.Start.OwnerId));
        if (record.End.Connected && record.End.OwnerId >= 0) ids.Add(new ElementId(record.End.OwnerId));
        FlexConduitV3Updater.RegisterElementTriggers(doc, ids);
    }
}
