using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwBridge.Models;

namespace SwBridge.Tools.Model;

/// <summary>
/// Reads the active SOLIDWORKS document via COM and produces a <see cref="ModelState"/> snapshot.
/// All SOLIDWORKS API interactions are isolated here; no formatting logic lives in this class.
/// </summary>
public sealed class ModelStateCollector
{
    // ── Public entry point ───────────────────────────────────────────────────

    public ModelState Collect(ISldWorks app)
    {
        var doc = TryGet(() => app.IActiveDoc2);
        if (doc is null)
            return ModelState.Create("(none)", null, "Error", false, null, null, null);

        var docTypeInt = TryGetInt(() => doc.GetType());
        var title      = TryGet(() => doc.GetTitle()) ?? "untitled";
        var filePath   = Normalize(TryGet(() => doc.GetPathName()));
        var unsaved    = TryGetBool(() => doc.GetSaveFlag());
        var docType    = MapDocumentType(docTypeInt);

        var (boundsMm, massG, volMm3) = CollectGeometry(doc);

        IReadOnlyList<FeatureInfo>   features   = [];
        IReadOnlyList<ComponentInfo> components = [];
        IReadOnlyList<MateInfo>      mates      = [];

        switch (docTypeInt)
        {
            case (int)swDocumentTypes_e.swDocPART:
                features = CollectFeatures(doc);
                break;

            case (int)swDocumentTypes_e.swDocASSEMBLY when doc is IAssemblyDoc asm:
                components = CollectComponents(asm);
                mates      = CollectMates(doc, asm);
                break;
        }

        return ModelState.Create(title, filePath, docType, unsaved, boundsMm, massG, volMm3,
            features, components, mates);
    }

    // ── Geometry (bounding box + mass) ───────────────────────────────────────

    private static (double[]? BoundsMm, double? MassG, double? VolMm3) CollectGeometry(IModelDoc2 doc)
    {
        double[]? bounds = null;
        double?   massG  = null;
        double?   volMm3 = null;

        try
        {
            IEnumerable<IBody2> bodies = doc switch
            {
                IPartDoc part =>
                    (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[])
                    ?.OfType<IBody2>() ?? [],

                IAssemblyDoc asm =>
                    ((asm.GetComponents(false) as object[])?.OfType<IComponent2>() ?? [])
                    .SelectMany(c =>
                        (c.GetBodies2((int)swBodyType_e.swSolidBody) as object[])
                        ?.OfType<IBody2>() ?? []),

                _ => []
            };

            var box = AggregateBoundingBoxes(bodies);
            if (box is not null)
                bounds = [(box[3] - box[0]) * 1000, (box[4] - box[1]) * 1000, (box[5] - box[2]) * 1000];
        }
        catch { }

        try
        {
            var mp = TryGet(() => doc.Extension.CreateMassProperty() as IMassProperty);
            if (mp is not null)
            {
                double m = TryGetDouble(() => mp.Mass);
                double v = TryGetDouble(() => mp.Volume);
                if (m > 0) massG  = m * 1000;
                if (v > 0) volMm3 = v * 1e6;
            }
        }
        catch { }

        return (bounds, massG, volMm3);
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

    // ── Feature collection (Parts) ───────────────────────────────────────────

    private static readonly HashSet<string> SkipFeatureTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "HistoryFolder", "DetailCabinet", "CommentsFolder", "SelectionSetFolder",
            "Attribute", "SensorFolder", "MarkupFolder", "FavoriteFolder",
            "BlockDef", "OriginProfileFeature", "3DAnnotationFolder", "MateGroup"
        };

    private static List<FeatureInfo> CollectFeatures(IModelDoc2 doc)
    {
        var planeMap = BuildPlaneMap(doc);
        var result   = new List<FeatureInfo>();
        var feat     = TryGet(() => doc.FirstFeature() as IFeature);

        while (feat is not null)
        {
            var typeName = TryGet(() => feat.GetTypeName2()) ?? string.Empty;

            if (!SkipFeatureTypes.Contains(typeName))
            {
                var name       = TryGet(() => feat.Name) ?? string.Empty;
                var suppressed = TryGetBool(() =>
                    (bool)feat.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null));
                var dims    = CollectDimensions(feat);
                var sketch  = IsSketchType(typeName)  ? TryCollectSketchDetail(feat, planeMap) : null;
                var extrude = IsExtrudeType(typeName) ? TryCollectExtrudeDetail(feat, doc)     : null;

                result.Add(new FeatureInfo(name, MapFeatureType(typeName), suppressed, dims, sketch, extrude));
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
                var fullName  = TryGet(() => dim.FullName) ?? TryGet(() => dim.Name) ?? string.Empty;
                var shortName = fullName.Contains('@') ? fullName[..fullName.IndexOf('@')] : fullName;
                double raw    = TryGetDouble(() => dim.Value);
                bool isAngle  = TryGetInt(() => dim.GetType()) ==
                                (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
                var valStr    = isAngle
                    ? $"{raw * (180.0 / Math.PI):F2}°"
                    : $"{raw * 1000:F2}mm";

                if (!string.IsNullOrWhiteSpace(shortName))
                    dims.Add(new DimInfo(shortName, valStr));
            }

            dispDim = TryGet(() => feat.GetNextDisplayDimension(dispDim) as IDisplayDimension);
        }

        return dims;
    }

    // ── Plane map ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans for RefPlane features and maps each plane's COM identity → feature name.
    /// Used by sketch collection to resolve reference plane names.
    /// </summary>
    private static Dictionary<IntPtr, string> BuildPlaneMap(IModelDoc2 doc)
    {
        var map  = new Dictionary<IntPtr, string>();
        var feat = TryGet(() => doc.FirstFeature() as IFeature);

        while (feat is not null)
        {
            if (string.Equals(TryGet(() => feat.GetTypeName2()), "RefPlane",
                    StringComparison.OrdinalIgnoreCase))
            {
                var plane = TryGet(() => feat.GetSpecificFeature2() as IRefPlane);
                if (plane is not null)
                    try
                    {
                        IntPtr ptr = Marshal.GetIUnknownForObject(plane);
                        Marshal.Release(ptr);
                        map.TryAdd(ptr, TryGet(() => feat.Name) ?? "plane");
                    }
                    catch { }
            }

            feat = TryGet(() => feat.GetNextFeature() as IFeature);
        }

        return map;
    }

    // ── Sketch detail ────────────────────────────────────────────────────────

    private static SketchInfo? TryCollectSketchDetail(
        IFeature feat,
        Dictionary<IntPtr, string> planeMap)
    {
        try
        {
            var sketch = TryGet(() => feat.GetSpecificFeature2() as ISketch);
            if (sketch is null) return null;

            // Reference plane name and world-space normal
            var planeName = "?";
            double[]? normal = null;

            int entityType = 0;
            object? refEntity = null;
            try { refEntity = sketch.GetReferenceEntity(ref entityType); } catch { }

            if (refEntity is IRefPlane refPlane)
            {
                try
                {
                    IntPtr ptr = Marshal.GetIUnknownForObject(refPlane);
                    Marshal.Release(ptr);
                    planeName = planeMap.TryGetValue(ptr, out var pn) ? pn : "plane";
                }
                catch { planeName = "plane"; }

                // Plane equation [a,b,c,d] — (a,b,c) is the unit normal
                if (TryGet(() => refPlane.GetRefPlaneParams() as double[]) is { Length: >= 3 } p)
                    normal = [p[0], p[1], p[2]];
            }

            // Sketch origin in world space via inverse of ModelToSketchTransform
            // The 16-element column-major 4×4 matrix has translation at [12,13,14] (metres)
            double[]? origin = null;
            var m2s = TryGet(() => sketch.ModelToSketchTransform as IMathTransform);
            var s2m = TryGet(() => m2s?.Inverse() as IMathTransform);
            if (TryGet(() => s2m?.ArrayData as double[]) is { Length: >= 15 } xf)
                origin = [xf[12] * 1000, xf[13] * 1000, xf[14] * 1000];

            // Constrained status
            var constrainedStatus = TryGetInt(() => sketch.GetConstrainedStatus()) switch
            {
                0 => "Under-Defined",
                1 => "Fully-Defined",
                2 => "Over-Defined",
                var n => $"status:{n}"
            };

            // Segment geometry + deduplicated relations
            var segments       = new List<SketchSegmentInfo>();
            var seenRelPtrs    = new HashSet<IntPtr>();
            var relationCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            var rawSegs = TryGet(() => sketch.GetSketchSegments() as object[]);
            if (rawSegs is not null)
            {
                foreach (var obj in rawSegs)
                {
                    if (obj is not ISketchSegment seg) continue;
                    bool isConst = TryGetBool(() => seg.ConstructionGeometry);

                    SketchSegmentInfo? info = obj switch
                    {
                        ISketchLine line => BuildLineInfo(line, isConst),
                        ISketchArc  arc  => BuildArcInfo(arc,  isConst),
                        ISketchEllipse e => BuildEllipseInfo(e, isConst),
                        ISketchSpline    => new SketchSegmentInfo("Spline", null, null, null, null, isConst),
                        _                => null
                    };

                    if (info is not null) segments.Add(info);
                    CollectSegmentRelations(seg, seenRelPtrs, relationCounts);
                }
            }

            var relations = relationCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value > 1 ? $"{kv.Key}×{kv.Value}" : kv.Key)
                .ToList();

            return new SketchInfo(planeName, normal, origin, constrainedStatus, segments, relations);
        }
        catch { return null; }
    }

    private static SketchSegmentInfo? BuildLineInfo(ISketchLine line, bool isConst)
    {
        var sp = TryGet(() => line.IGetStartPoint2());
        var ep = TryGet(() => line.IGetEndPoint2());
        return sp is not null && ep is not null
            ? new SketchSegmentInfo("Line", Pt(sp), Pt(ep), null, null, isConst)
            : null;
    }

    private static SketchSegmentInfo BuildArcInfo(ISketchArc arc, bool isConst)
    {
        var cp       = TryGet(() => arc.IGetCenterPoint2());
        var sp       = TryGet(() => arc.IGetStartPoint2());
        var ep       = TryGet(() => arc.IGetEndPoint2());
        double r     = TryGetDouble(() => arc.GetRadius()) * 1000;
        bool isCircle = TryGetInt(() => arc.IsCircle()) != 0;
        return new SketchSegmentInfo(
            isCircle ? "Circle" : "Arc",
            sp is not null ? Pt(sp) : null,
            ep is not null ? Pt(ep) : null,
            cp is not null ? Pt(cp) : null,
            r, isConst);
    }

    private static SketchSegmentInfo BuildEllipseInfo(ISketchEllipse ellipse, bool isConst)
    {
        var cp = TryGet(() => ellipse.IGetCenterPoint2());
        return new SketchSegmentInfo("Ellipse", null, null, cp is not null ? Pt(cp) : null, null, isConst);
    }

    private static void CollectSegmentRelations(
        ISketchSegment seg,
        HashSet<IntPtr> seen,
        Dictionary<string, int> counts)
    {
        var rels = TryGet(() => seg.GetRelations() as object[]);
        if (rels is null) return;

        foreach (var relObj in rels)
        {
            if (relObj is not ISketchRelation rel) continue;
            try
            {
                IntPtr ptr = Marshal.GetIUnknownForObject(rel);
                Marshal.Release(ptr);
                if (!seen.Add(ptr)) continue;

                var label = MapRelationType(TryGetInt(() => rel.GetRelationType()));
                if (label is not null)
                    counts[label] = counts.GetValueOrDefault(label) + 1;
            }
            catch { }
        }
    }

    // ── Extrude detail ───────────────────────────────────────────────────────

    private static ExtrudeInfo? TryCollectExtrudeDetail(IFeature feat, IModelDoc2 doc)
    {
        try
        {
            var data = TryGet(() => feat.GetDefinition() as IExtrudeFeatureData2);
            if (data is null) return null;

            bool accessed = false;
            try
            {
                accessed = TryGetBool(() => data.AccessSelections(doc, null));
                if (!accessed) return null;

                bool isBoss = TryGetBool(() => data.IsBossFeature());
                bool both   = TryGetBool(() => data.BothDirections);
                bool rev    = TryGetBool(() => data.ReverseDirection);

                int    ec1    = TryGetInt    (() => data.GetEndCondition(true));
                double depth1 = TryGetDouble (() => data.GetDepth(true))      * 1000;
                double draft1 = TryGetDouble (() => data.GetDraftAngle(true)) * (180.0 / Math.PI);

                int?    ec2    = null;
                double? depth2 = null;
                double? draft2 = null;
                if (both)
                {
                    ec2    = TryGetInt    (() => data.GetEndCondition(false));
                    depth2 = TryGetDouble (() => data.GetDepth(false))      * 1000;
                    draft2 = TryGetDouble (() => data.GetDraftAngle(false)) * (180.0 / Math.PI);
                }

                return new ExtrudeInfo(
                    isBoss ? "Boss" : "Cut",
                    MapEndCondition(ec1), depth1, draft1, both, rev,
                    ec2.HasValue ? MapEndCondition(ec2.Value) : null, depth2, draft2);
            }
            finally
            {
                if (accessed) try { data.ReleaseSelectionAccess(); } catch { }
            }
        }
        catch { return null; }
    }

    // ── Component collection (Assemblies) ────────────────────────────────────

    private static List<ComponentInfo> CollectComponents(IAssemblyDoc asm)
    {
        var raw = TryGet(() => asm.GetComponents(true) as object[])
               ?? TryGet(() => (object[])asm.GetComponents(true));
        if (raw is null) return [];

        var result = new List<ComponentInfo>(raw.Length);
        foreach (IComponent2 comp in raw.OfType<IComponent2>())
        {
            var name    = TryGet(() => comp.Name2) ?? TryGet(() => comp.Name) ?? "?";
            var srcPath = TryGet(() => comp.GetPathName()) ?? string.Empty;
            var srcFile = string.IsNullOrWhiteSpace(srcPath)
                ? string.Empty
                : Path.GetFileName(srcPath);
            var supp    = TryGetBool(() => comp.IsSuppressed());
            var hidden  = TryGetBool(() => comp.IsHidden(false));
            var state   = supp ? "suppressed" : hidden ? "hidden" : "active";
            result.Add(new ComponentInfo(name, srcFile, state));
        }

        return result;
    }

    // ── Mate collection (Assemblies) ─────────────────────────────────────────

    private static List<MateInfo> CollectMates(IModelDoc2 doc, IAssemblyDoc asm)
    {
        var bodyMap = BuildBodyComponentMap(asm);
        var mates   = new List<MateInfo>();
        var feat    = TryGet(() => doc.FirstFeature() as IFeature);

        while (feat is not null)
        {
            if (string.Equals(TryGet(() => feat.GetTypeName2()), "MateGroup",
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

        return mates;
    }

    /// <summary>
    /// Builds a COM-pointer → component-name map so mate entity faces can be resolved
    /// to a readable component name without walking the full tree per face.
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
            var bodies = TryGet(() => comp.GetBodies2((int)swBodyType_e.swSolidBody) as object[])
                      ?? TryGet(() => (object[])comp.GetBodies2((int)swBodyType_e.swSolidBody));
            if (bodies is null) continue;

            foreach (IBody2 body in bodies.OfType<IBody2>())
                try
                {
                    IntPtr ptr = Marshal.GetIUnknownForObject(body);
                    Marshal.Release(ptr);
                    map.TryAdd(ptr, name);
                }
                catch { }
        }

        return map;
    }

    private static MateInfo? TryCollectMate(IFeature mateFeat, Dictionary<IntPtr, string> bodyMap)
    {
        try
        {
            var mate = TryGet(() => mateFeat.GetSpecificFeature2() as IMate2);
            if (mate is null) return null;

            var mateType  = (swMateType_e)TryGetInt(() => mate.Type);
            var mateName  = TryGet(() => mateFeat.Name) ?? "?";
            int count     = TryGetInt(() => mate.GetMateEntityCount());

            var entity1 = count > 0 ? ResolveEntity(TryGet(() => mate.MateEntity(0) as IMateEntity2), bodyMap) : "?";
            var entity2 = count > 1 ? ResolveEntity(TryGet(() => mate.MateEntity(1) as IMateEntity2), bodyMap) : "?";

            // Dimensional value for Distance / Angle mates
            double? value   = null;
            bool    isAngle = mateType == swMateType_e.swMateANGLE;
            if (mateType is swMateType_e.swMateDISTANCE or swMateType_e.swMateANGLE)
            {
                var dim = TryGet(() =>
                    (mateFeat.GetFirstDisplayDimension() as IDisplayDimension)
                    ?.GetDimension2(0) as IDimension);
                if (dim is not null)
                {
                    double raw = TryGetDouble(() => dim.Value);
                    value = isAngle ? raw * (180.0 / Math.PI) : raw * 1000;
                }
            }

            // Alignment: 0=Aligned, 1=Anti-Aligned, 2=Closest (swMateAlign_e)
            var alignment = TryGetInt(() => mate.Alignment) switch
            {
                1 => "Anti-Aligned",
                2 => "Closest",
                _ => "Aligned"
            };

            return new MateInfo(
                mateName, MapMateType(mateType),
                entity1, entity2, value, isAngle,
                alignment, TryGetBool(() => mate.Flipped));
        }
        catch { return null; }
    }

    // ── Entity resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a mate entity to "componentName::featureName [SurfaceType, face[N]/Total, Xmm²]".
    /// </summary>
    private static string ResolveEntity(IMateEntity2? entity, Dictionary<IntPtr, string> bodyMap)
    {
        if (entity is null) return "?";
        try
        {
            var reference = TryGet(() => entity.Reference);
            if (reference is null) return "?";

            if (reference is IFace2 face)
            {
                var feat        = TryGet(() => face.GetFeature() as IFeature);
                var featName    = feat is not null ? TryGet(() => feat.Name) ?? string.Empty : string.Empty;
                var compName    = GetComponentName(TryGet(() => face.IGetBody()), bodyMap);
                var desc        = string.IsNullOrWhiteSpace(featName) ? compName : $"{compName}::{featName}";
                var surface     = TryGet(() => face.IGetSurface());
                var surfLabel   = surface is not null ? GetSurfaceTypeLabel(surface) : "Face";
                var faceDetail  = GetFaceIndexAndArea(face, feat);
                return $"{desc} [{surfLabel}{faceDetail}]";
            }

            if (reference is IEdge edge)
            {
                var adjacent = TryGet(() => edge.GetTwoAdjacentFaces2() as object[]);
                if (adjacent?.Length > 0 && adjacent[0] is IFace2 adjFace)
                    return $"{GetComponentName(TryGet(() => adjFace.IGetBody()), bodyMap)} [Edge]";
                return "[Edge]";
            }

            if (reference is IRefPlane)
            {
                var compName = TryGet(() => entity.ReferenceComponent?.Name) ?? string.Empty;
                return string.IsNullOrWhiteSpace(compName) ? "[Datum Plane]" : $"{compName} [Datum Plane]";
            }

            if (reference is IRefAxis)
            {
                var compName = TryGet(() => entity.ReferenceComponent?.Name) ?? string.Empty;
                return string.IsNullOrWhiteSpace(compName) ? "[Datum Axis]" : $"{compName} [Datum Axis]";
            }

            return reference.GetType().Name;
        }
        catch { return "?"; }
    }

    private static string GetComponentName(IBody2? body, Dictionary<IntPtr, string> bodyMap)
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

    private static string GetSurfaceTypeLabel(ISurface s)
    {
        try
        {
            if (s.IsPlane())    return "Plane";
            if (s.IsCylinder()) return "Cylinder";
            if (s.IsCone())     return "Cone";
            if (s.IsSphere())   return "Sphere";
            if (s.IsTorus())    return "Torus";
            if (s.IsRevolved()) return "Revolved";
            if (s.IsSwept())    return "Swept";
            return "Face";
        }
        catch { return "Face"; }
    }

    /// <summary>
    /// Returns ", face[N]/Total, Xmm²" — the index into feat.GetFaces() and area for verification.
    /// </summary>
    private static string GetFaceIndexAndArea(IFace2 face, IFeature? feat)
    {
        try
        {
            double areaMm2 = TryGetDouble(() => face.GetArea()) * 1e6;
            if (feat is null) return $", {areaMm2:F1}mm²";

            int faceIndex  = -1;
            int totalFaces = TryGetInt(() => feat.GetFaceCount());
            var featFaces  = TryGet(() => feat.GetFaces() as object[]);

            if (featFaces is not null)
                for (int i = 0; i < featFaces.Length; i++)
                    if (featFaces[i] is IFace2 f && TryGetBool(() => face.IsSame(f)))
                    { faceIndex = i; break; }

            return faceIndex >= 0
                ? $", face[{faceIndex}]/{totalFaces}, {areaMm2:F1}mm²"
                : $", {areaMm2:F1}mm²";
        }
        catch { return string.Empty; }
    }

    // ── Type maps ────────────────────────────────────────────────────────────

    private static string MapDocumentType(int t) => t switch
    {
        (int)swDocumentTypes_e.swDocPART     => "Part",
        (int)swDocumentTypes_e.swDocASSEMBLY => "Assembly",
        (int)swDocumentTypes_e.swDocDRAWING  => "Drawing",
        _                                    => $"Unknown({t})"
    };

    private static bool IsSketchType(string t) =>
        t.Equals("ProfileFeature", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("3DSketch",       StringComparison.OrdinalIgnoreCase);

    private static bool IsExtrudeType(string t) =>
        t.Equals("Extrusion",  StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Cut",        StringComparison.OrdinalIgnoreCase) ||
        t.Equals("ICEExtrude", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("ICECut",     StringComparison.OrdinalIgnoreCase);

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

    private static string MapEndCondition(int ec) => ec switch
    {
        0 => "Blind",
        1 => "Through-All",
        2 => "Through-All-Both",
        3 => "Through-Next",
        4 => "Up-To-Vertex",
        5 => "Up-To-Surface",
        6 => "Offset-From-Surface",
        7 => "Up-To-Body",
        8 => "Mid-Plane",
        _ => $"EndCond:{ec}"
    };

    private static string? MapRelationType(int t) => t switch
    {
        1  => "Distance",
        2  => "Angle",
        3  => "Radius",
        4  => "Horizontal",
        5  => "Vertical",
        6  => "Tangent",
        7  => "Parallel",
        8  => "Perpendicular",
        9  => "Coincident",
        10 => "Concentric",
        11 => "Symmetric",
        12 => "Midpoint",
        13 => "Intersection",
        14 => "Equal",
        15 => "Diameter",
        17 => "Fixed",
        25 => "HorizPoints",
        26 => "VertPoints",
        27 => "Collinear",
        40 => "Pierce",
        42 => "MergePoints",
        44 => "ArcLength",
        45 => "Normal",
        _  => null     // snap / grid / internal types — not useful to the LLM
    };

    // ── COM helpers ──────────────────────────────────────────────────────────

    private static T? TryGet<T>(Func<T?> fn) where T : class
    {
        try { return fn(); }
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

    /// <summary>Converts a sketch point to a [x, y, z] mm array in model (world) space.</summary>
    private static double[]? Pt(ISketchPoint? p) =>
        p is null ? null :
        [TryGetDouble(() => p.X) * 1000,
         TryGetDouble(() => p.Y) * 1000,
         TryGetDouble(() => p.Z) * 1000];

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
