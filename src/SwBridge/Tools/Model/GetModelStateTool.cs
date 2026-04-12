using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwBridge.Connection;

namespace SwBridge.Tools.Model;

/// <summary>
/// Returns a human-readable snapshot of the active SOLIDWORKS document for LLM context.
/// For parts: document info, bounding box, mass, and the full feature tree with dimension values.
/// For assemblies: document info, bounding box, mass, component tree, and mates with resolved entity names.
/// </summary>
public sealed class GetModelStateTool : ISwTool
{
    private readonly ISwConnector _connector;

    /// <summary>
    /// Initializes the tool with the active SOLIDWORKS connector.
    /// </summary>
    public GetModelStateTool(ISwConnector connector)
    {
        _connector = connector;
    }

    /// <inheritdoc/>
    public string Name => "get_model_state";

    /// <inheritdoc/>
    public string Description =>
        "Returns a structured summary of the active SOLIDWORKS document. " +
        "For parts: feature tree with dimension values, bounding box, and mass properties. " +
        "For assemblies: component tree, mates with resolved face/entity names, bounding box, and mass. " +
        "No parameters required. " +
        "Call this before writing any revision or inspection macro so you have accurate feature names, " +
        "dimension values, and structural context.";

    /// <inheritdoc/>
    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        if (_connector.State != SwConnectionState.Ready || _connector.Application is null)
            return Task.FromResult(
                $"ERROR: SOLIDWORKS is not connected. Current state: {_connector.State}.");

        return Task.Run(() => Collect(_connector.Application), cancellationToken);
    }

    // ── Top-level collector ──────────────────────────────────────────────────

    private string Collect(ISldWorks app)
    {
        var doc = TryGet(() => app.IActiveDoc2);
        if (doc is null)
            return "ERROR: No document is currently open in SOLIDWORKS.";

        var sb      = new StringBuilder();
        var docType = TryGetInt(() => doc.GetType());

        AppendDocumentHeader(sb, doc, docType);
        AppendGeometrySummary(sb, doc);
        sb.AppendLine();

        switch (docType)
        {
            case (int)swDocumentTypes_e.swDocPART:
                AppendFeatureTree(sb, doc);
                break;

            case (int)swDocumentTypes_e.swDocASSEMBLY when doc is IAssemblyDoc asm:
                AppendComponentTree(sb, asm);
                sb.AppendLine();
                AppendMates(sb, doc, asm);
                break;

            case (int)swDocumentTypes_e.swDocDRAWING:
                sb.AppendLine("Drawing document — feature tree not applicable.");
                break;

            default:
                sb.AppendLine($"Unknown document type ({docType}).");
                break;
        }

        return sb.ToString().TrimEnd();
    }

    // ── Document header ──────────────────────────────────────────────────────

    private static void AppendDocumentHeader(StringBuilder sb, IModelDoc2 doc, int docType)
    {
        var title     = TryGet(() => doc.GetTitle()) ?? "untitled";
        var path      = TryGet(() => doc.GetPathName());
        var modified  = TryGetBool(() => doc.GetSaveFlag()) ? ", unsaved" : "";
        var typeLabel = docType switch
        {
            (int)swDocumentTypes_e.swDocPART     => "Part",
            (int)swDocumentTypes_e.swDocASSEMBLY => "Assembly",
            (int)swDocumentTypes_e.swDocDRAWING  => "Drawing",
            _                                    => $"Document({docType})"
        };

        sb.AppendLine($"Document: {title}  ({typeLabel}{modified})");

        if (!string.IsNullOrWhiteSpace(path))
            sb.AppendLine($"Path:     {path}");
    }

    // ── Geometry summary (bounding box + mass) ───────────────────────────────

    private static void AppendGeometrySummary(StringBuilder sb, IModelDoc2 doc)
    {
        // Bounding box: aggregate body boxes from the document's solid bodies.
        var box = GetDocumentBoundingBox(doc);
        if (box is not null)
        {
            double dx = (box[3] - box[0]) * 1000;
            double dy = (box[4] - box[1]) * 1000;
            double dz = (box[5] - box[2]) * 1000;
            sb.AppendLine($"Bounds:   {dx:F1} × {dy:F1} × {dz:F1} mm");
        }

        // Mass properties
        try
        {
            var massProp = TryGet(() => doc.Extension.CreateMassProperty() as IMassProperty);
            if (massProp is not null)
            {
                double massKg = TryGetDouble(() => massProp.Mass);
                double volM3  = TryGetDouble(() => massProp.Volume);

                if (massKg > 0 || volM3 > 0)
                    sb.AppendLine($"Mass:     {massKg * 1000:F1} g  |  Volume: {volM3 * 1e6:F0} mm³");
            }
        }
        catch { /* mass properties unavailable for some document states */ }
    }

    /// <summary>
    /// Aggregates IBody2.GetBodyBox() across all solid bodies in the document.
    /// Works for both part documents (via IPartDoc) and assemblies (via IAssemblyDoc components).
    /// Returns [xmin, ymin, zmin, xmax, ymax, zmax] in metres, or null if unavailable.
    /// </summary>
    private static double[]? GetDocumentBoundingBox(IModelDoc2 doc)
    {
        try
        {
            IEnumerable<IBody2> bodies;

            if (doc is IPartDoc partDoc)
            {
                var raw = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                bodies = raw?.OfType<IBody2>() ?? [];
            }
            else if (doc is IAssemblyDoc asm)
            {
                var comps = (asm.GetComponents(false) as object[])?.OfType<IComponent2>() ?? [];
                bodies = comps
                    .SelectMany(c =>
                        (c.GetBodies2((int)swBodyType_e.swSolidBody) as object[])
                        ?.OfType<IBody2>() ?? []);
            }
            else
            {
                return null;
            }

            return AggregateBoundingBoxes(bodies);
        }
        catch { return null; }
    }

    private static double[]? AggregateBoundingBoxes(IEnumerable<IBody2> bodies)
    {
        double xmin = double.MaxValue, ymin = double.MaxValue, zmin = double.MaxValue;
        double xmax = double.MinValue, ymax = double.MinValue, zmax = double.MinValue;
        bool   any  = false;

        foreach (var body in bodies)
        {
            if (body.GetBodyBox() is not double[] b || b.Length < 6) continue;
            xmin = Math.Min(xmin, b[0]); ymin = Math.Min(ymin, b[1]); zmin = Math.Min(zmin, b[2]);
            xmax = Math.Max(xmax, b[3]); ymax = Math.Max(ymax, b[4]); zmax = Math.Max(zmax, b[5]);
            any = true;
        }

        return any ? [xmin, ymin, zmin, xmax, ymax, zmax] : null;
    }

    // ── Part: feature tree ───────────────────────────────────────────────────

    private static void AppendFeatureTree(StringBuilder sb, IModelDoc2 doc)
    {
        var features = CollectFeatures(doc);

        if (features.Count == 0)
        {
            sb.AppendLine("Feature Tree: (empty)");
            return;
        }

        sb.AppendLine($"Feature Tree ({features.Count} features):");

        for (int i = 0; i < features.Count; i++)
        {
            var f       = features[i];
            var suppTag = f.Suppressed ? "  [suppressed]" : string.Empty;
            var dimTag  = f.Dimensions.Count > 0
                ? "  " + string.Join(", ", f.Dimensions.Select(d => $"{d.Name}={d.Value}"))
                : string.Empty;

            sb.AppendLine($"  [{i + 1,2}]  {f.Name,-28}  {f.TypeLabel,-20}{dimTag}{suppTag}");
        }
    }

    private static List<FeatureInfo> CollectFeatures(IModelDoc2 doc)
    {
        // Internal SW housekeeping types — not useful to the LLM.
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HistoryFolder", "DetailCabinet", "CommentsFolder", "SelectionSetFolder",
            "Attribute", "SensorFolder", "MarkupFolder", "FavoriteFolder",
            "BlockDef", "OriginProfileFeature", "3DAnnotationFolder", "MateGroup"
        };

        var result = new List<FeatureInfo>();
        var feat   = TryGet(() => doc.FirstFeature() as IFeature);

        while (feat is not null)
        {
            var typeName = TryGet(() => feat.GetTypeName2()) ?? string.Empty;

            if (!skip.Contains(typeName))
            {
                var name       = TryGet(() => feat.Name) ?? string.Empty;
                // IsSuppressed2 returns object in the interop — cast explicitly.
                var suppressed = TryGetBool(() =>
                    (bool)feat.IsSuppressed2(
                        (int)swInConfigurationOpts_e.swThisConfiguration, null));
                var typeLabel  = MapFeatureType(typeName);
                var dims       = CollectDimensions(feat);

                result.Add(new FeatureInfo(name, typeLabel, suppressed, dims));
            }

            feat = TryGet(() => feat.GetNextFeature() as IFeature);
        }

        return result;
    }

    private static List<DimInfo> CollectDimensions(IFeature feat)
    {
        var dims    = new List<DimInfo>();
        var dispDim = TryGet(() => feat.GetFirstDisplayDimension() as IDisplayDimension);

        while (dispDim is not null)
        {
            var dim = TryGet(() => dispDim.GetDimension2(0) as IDimension);
            if (dim is not null)
            {
                // Full name is "D1@Boss-Extrude1" — strip the feature-name suffix.
                var fullName  = TryGet(() => dim.FullName) ?? TryGet(() => dim.Name) ?? string.Empty;
                var shortName = fullName.Contains('@')
                    ? fullName[..fullName.IndexOf('@')]
                    : fullName;

                double raw     = TryGetDouble(() => dim.Value);
                // IDimension.GetType() returns swDimensionParamType_e as int.
                int    dimType = TryGetInt(() => dim.GetType());
                bool   isAngle = dimType == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
                var    valStr  = isAngle
                    ? $"{raw * (180.0 / Math.PI):F2}°"
                    : $"{raw * 1000:F2}mm";

                if (!string.IsNullOrWhiteSpace(shortName))
                    dims.Add(new DimInfo(shortName, valStr));
            }

            dispDim = TryGet(() => feat.GetNextDisplayDimension(dispDim) as IDisplayDimension);
        }

        return dims;
    }

    // ── Assembly: component tree ─────────────────────────────────────────────

    private static void AppendComponentTree(StringBuilder sb, IAssemblyDoc asm)
    {
        // Top-level only — sub-assembly contents are accessible by activating
        // the sub-assembly and calling get_model_state again.
        var raw = TryGet(() => asm.GetComponents(true) as object[])
               ?? TryGet(() => (object[])asm.GetComponents(true));

        if (raw is null || raw.Length == 0)
        {
            sb.AppendLine("Components: (none)");
            return;
        }

        sb.AppendLine($"Components ({raw.Length} top-level):");

        foreach (IComponent2 comp in raw.OfType<IComponent2>())
        {
            var name       = TryGet(() => comp.Name2) ?? TryGet(() => comp.Name) ?? "?";
            var srcPath    = TryGet(() => comp.GetPathName()) ?? string.Empty;
            var srcFile    = string.IsNullOrWhiteSpace(srcPath) ? string.Empty : Path.GetFileName(srcPath);
            var suppressed = TryGetBool(() => comp.IsSuppressed());
            var hidden     = TryGetBool(() => comp.IsHidden(false));
            var state      = suppressed ? "suppressed" : hidden ? "hidden" : "active";

            sb.AppendLine($"  {name,-30}  {srcFile,-30}  {state}");
        }
    }

    // ── Assembly: mates ──────────────────────────────────────────────────────

    private static void AppendMates(StringBuilder sb, IModelDoc2 doc, IAssemblyDoc asm)
    {
        var bodyMap = BuildBodyComponentMap(asm);
        var mates   = new List<MateInfo>();

        var feat = TryGet(() => doc.FirstFeature() as IFeature);
        while (feat is not null)
        {
            if (string.Equals(
                    TryGet(() => feat.GetTypeName2()),
                    "MateGroup",
                    StringComparison.OrdinalIgnoreCase))
            {
                var sub = TryGet(() => feat.GetFirstSubFeature() as IFeature);
                while (sub is not null)
                {
                    var mate = TryCollectMate(sub, bodyMap);
                    if (mate is not null) mates.Add(mate);
                    sub = TryGet(() => sub.GetNextSubFeature() as IFeature);
                }
            }

            feat = TryGet(() => feat.GetNextFeature() as IFeature);
        }

        if (mates.Count == 0)
        {
            sb.AppendLine("Mates: (none)");
            return;
        }

        sb.AppendLine($"Mates ({mates.Count}):");
        foreach (var m in mates)
        {
            var valStr = m.Value.HasValue
                ? $"  {m.Value.Value:F2}{(m.IsAngle ? "°" : "mm")}"
                : string.Empty;
            sb.AppendLine(
                $"  {m.Name,-22}  {m.TypeLabel,-16}  {m.Entity1} ↔ {m.Entity2}{valStr}");
        }
    }

    /// <summary>
    /// Builds a COM-pointer → component-name map so mate entities can be resolved
    /// to a readable component name without walking the full component tree per face.
    /// </summary>
    private static Dictionary<IntPtr, string> BuildBodyComponentMap(IAssemblyDoc asm)
    {
        var map = new Dictionary<IntPtr, string>();

        var raw = TryGet(() => asm.GetComponents(false) as object[])
               ?? TryGet(() => (object[])asm.GetComponents(false));
        if (raw is null) return map;

        foreach (IComponent2 comp in raw.OfType<IComponent2>())
        {
            var name   = TryGet(() => comp.Name2) ?? TryGet(() => comp.Name) ?? "?";
            // GetBodies2 on IComponent2 takes one argument (BodyType) in SW 2026 interop.
            var bodies = TryGet(() => comp.GetBodies2((int)swBodyType_e.swSolidBody) as object[])
                      ?? TryGet(() => (object[])comp.GetBodies2((int)swBodyType_e.swSolidBody));
            if (bodies is null) continue;

            foreach (IBody2 body in bodies.OfType<IBody2>())
            {
                try
                {
                    IntPtr ptr = Marshal.GetIUnknownForObject(body);
                    Marshal.Release(ptr);
                    map.TryAdd(ptr, name);
                }
                catch { /* skip if COM identity unavailable */ }
            }
        }

        return map;
    }

    private static MateInfo? TryCollectMate(
        IFeature mateFeat,
        Dictionary<IntPtr, string> bodyMap)
    {
        try
        {
            var mate = TryGet(() => mateFeat.GetSpecificFeature2() as IMate2);
            if (mate is null) return null;

            var mateType  = (swMateType_e)TryGetInt(() => mate.Type);
            var typeLabel = MapMateType(mateType);
            var mateName  = TryGet(() => mateFeat.Name) ?? "?";

            int count   = TryGetInt(() => mate.GetMateEntityCount());
            // IMate2.MateEntity(int Index) is the correct method name in SW 2026 interop.
            var entity1 = count > 0
                ? ResolveEntity(TryGet(() => mate.MateEntity(0) as IMateEntity2), bodyMap)
                : "?";
            var entity2 = count > 1
                ? ResolveEntity(TryGet(() => mate.MateEntity(1) as IMateEntity2), bodyMap)
                : "?";

            // Dimensional value for Distance / Angle mates
            double? value   = null;
            bool    isAngle = mateType == swMateType_e.swMateANGLE;

            if (mateType is swMateType_e.swMateDISTANCE or swMateType_e.swMateANGLE)
            {
                var dispDim = TryGet(() => mateFeat.GetFirstDisplayDimension() as IDisplayDimension);
                if (dispDim is not null)
                {
                    var dim = TryGet(() => dispDim.GetDimension2(0) as IDimension);
                    if (dim is not null)
                    {
                        double raw = TryGetDouble(() => dim.Value);
                        value = isAngle ? raw * (180.0 / Math.PI) : raw * 1000;
                    }
                }
            }

            return new MateInfo(mateName, typeLabel, entity1, entity2, value, isAngle);
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolves a mate entity reference to a readable "componentName::featureName" string.
    /// Falls back gracefully for reference planes, axes, and edges.
    /// </summary>
    private static string ResolveEntity(
        IMateEntity2? entity,
        Dictionary<IntPtr, string> bodyMap)
    {
        if (entity is null) return "?";

        try
        {
            var reference = TryGet(() => entity.Reference);
            if (reference is null) return "?";

            if (reference is IFace2 face)
            {
                var featureName = TryGet(() => (face.GetFeature() as IFeature)?.Name)
                               ?? string.Empty;
                var bodyName    = GetComponentName(TryGet(() => face.IGetBody()), bodyMap);
                return string.IsNullOrWhiteSpace(featureName)
                    ? bodyName
                    : $"{bodyName}::{featureName}";
            }

            if (reference is IEdge edge)
            {
                var adjacent = TryGet(() => edge.GetTwoAdjacentFaces2() as object[]);
                if (adjacent?.Length > 0 && adjacent[0] is IFace2 adjFace)
                {
                    var compName = GetComponentName(TryGet(() => adjFace.IGetBody()), bodyMap);
                    return $"{compName}::edge";
                }
                return "edge";
            }

            // Reference planes and axes have no Name property in this interop version —
            // use the component name from ReferenceComponent if available.
            if (reference is IRefPlane)
            {
                var compName = TryGet(() => entity.ReferenceComponent?.Name) ?? string.Empty;
                return string.IsNullOrWhiteSpace(compName) ? "plane" : $"{compName}::plane";
            }

            if (reference is IRefAxis)
            {
                var compName = TryGet(() => entity.ReferenceComponent?.Name) ?? string.Empty;
                return string.IsNullOrWhiteSpace(compName) ? "axis" : $"{compName}::axis";
            }

            return reference.GetType().Name;
        }
        catch { return "?"; }
    }

    private static string GetComponentName(
        IBody2? body,
        Dictionary<IntPtr, string> bodyMap)
    {
        if (body is null) return "?";
        try
        {
            IntPtr ptr = Marshal.GetIUnknownForObject(body);
            Marshal.Release(ptr);
            return bodyMap.TryGetValue(ptr, out var name) ? name : "?";
        }
        catch { return "?"; }
    }

    // ── Feature type labels ──────────────────────────────────────────────────

    private static string MapFeatureType(string t) => t switch
    {
        "Extrusion"      => "Boss-Extrude",
        "Cut"            => "Cut-Extrude",
        "ICEExtrude"     => "Boss-Extrude",
        "ICECut"         => "Cut-Extrude",
        "Fillet"         => "Fillet",
        "Chamfer"        => "Chamfer",
        "RevolveCut"     => "Revolve-Cut",
        "RevolveExtrude" => "Boss-Revolve",
        "Sweep"          => "Sweep",
        "SweepCut"       => "Sweep-Cut",
        "Loft"           => "Loft",
        "LoftCut"        => "Loft-Cut",
        "Shell"          => "Shell",
        "CirPattern"     => "Circular Pattern",
        "LPattern"       => "Linear Pattern",
        "Mirror"         => "Mirror",
        "Rib"            => "Rib",
        "Draft"          => "Draft",
        "Scale"          => "Scale",
        "Dome"           => "Dome",
        "Wrap"           => "Wrap",
        "Indent"         => "Indent",
        "Combine"        => "Combine",
        "Flex"           => "Flex",
        "WeldBead"       => "Weld Bead",
        "HoleWzd"        => "Hole Wizard",
        "Thread"         => "Thread",
        "ProfileFeature" => "Sketch",
        "3DSketch"       => "3D Sketch",
        "RefPlane"       => "Reference Plane",
        "RefAxis"        => "Reference Axis",
        "RefPoint"       => "Reference Point",
        "CoordSys"       => "Coordinate System",
        _                => t
    };

    // ── Mate type labels ─────────────────────────────────────────────────────

    private static string MapMateType(swMateType_e t) => t switch
    {
        swMateType_e.swMateCOINCIDENT    => "Coincident",
        swMateType_e.swMateCONCENTRIC    => "Concentric",
        swMateType_e.swMateDISTANCE      => "Distance",
        swMateType_e.swMateANGLE         => "Angle",
        swMateType_e.swMatePARALLEL      => "Parallel",
        swMateType_e.swMatePERPENDICULAR => "Perpendicular",
        swMateType_e.swMateTANGENT       => "Tangent",
        swMateType_e.swMateSYMMETRIC     => "Symmetric",
        swMateType_e.swMateCAMFOLLOWER   => "Cam",
        swMateType_e.swMateGEAR          => "Gear",
        swMateType_e.swMateRACKPINION    => "Rack & Pinion",
        swMateType_e.swMateSCREW         => "Screw",
        swMateType_e.swMateWIDTH         => "Width",
        swMateType_e.swMatePROFILECENTER => "Profile Center",
        swMateType_e.swMateLINEARCOUPLER => "Linear Coupler",
        swMateType_e.swMatePATH          => "Path",
        _                                => $"Mate({(int)t})"
    };

    // ── COM helpers ──────────────────────────────────────────────────────────

    private static T? TryGet<T>(Func<T?> fn) where T : class
    {
        try { return fn(); }
        catch (COMException) { return null; }
        catch (InvalidCastException) { return null; }
        catch { return null; }
    }

    private static int TryGetInt(Func<int> fn)
    {
        try { return fn(); }
        catch { return 0; }
    }

    private static bool TryGetBool(Func<bool> fn)
    {
        try { return fn(); }
        catch { return false; }
    }

    private static double TryGetDouble(Func<double> fn)
    {
        try { return fn(); }
        catch { return 0.0; }
    }

    // ── Data records ─────────────────────────────────────────────────────────

    private sealed record FeatureInfo(
        string Name,
        string TypeLabel,
        bool Suppressed,
        List<DimInfo> Dimensions);

    private sealed record DimInfo(string Name, string Value);

    private sealed record MateInfo(
        string Name,
        string TypeLabel,
        string Entity1,
        string Entity2,
        double? Value,
        bool IsAngle);
}
