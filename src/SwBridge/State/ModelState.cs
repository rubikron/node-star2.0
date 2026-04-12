using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwBridge.Models;

/// <summary>
/// Snapshot of the active SOLIDWORKS document's model structure.
/// Call <see cref="ToText"/> for the human-readable LLM payload or
/// <see cref="ToJson"/> for the structured JSON representation.
/// </summary>
public sealed record ModelState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false
    };

    [JsonPropertyName("snapshot")]
    public string SnapshotToken { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string? FilePath { get; init; }

    [JsonPropertyName("docType")]
    public string DocType { get; init; } = string.Empty;

    [JsonPropertyName("unsaved")]
    public bool Unsaved { get; init; }

    /// <summary>Bounding box extents [dx, dy, dz] in mm.</summary>
    [JsonPropertyName("boundsMm")]
    public double[]? BoundsMm { get; init; }

    [JsonPropertyName("massG")]
    public double? MassGrams { get; init; }

    [JsonPropertyName("volumeMm3")]
    public double? VolumeMm3 { get; init; }

    /// <summary>Feature tree — populated for Part documents.</summary>
    [JsonPropertyName("features")]
    public IReadOnlyList<FeatureInfo> Features { get; init; } = [];

    /// <summary>Top-level component list — populated for Assembly documents.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyList<ComponentInfo> Components { get; init; } = [];

    /// <summary>Mate list — populated for Assembly documents.</summary>
    [JsonPropertyName("mates")]
    public IReadOnlyList<MateInfo> Mates { get; init; } = [];

    // ── Factory ───────────────────────────────────────────────────────────────

    public static ModelState Create(
        string                      title,
        string?                     filePath,
        string                      docType,
        bool                        unsaved,
        double[]?                   boundsMm,
        double?                     massGrams,
        double?                     volumeMm3,
        IReadOnlyList<FeatureInfo>?  features   = null,
        IReadOnlyList<ComponentInfo>? components = null,
        IReadOnlyList<MateInfo>?    mates      = null)
    {
        var s = new ModelState
        {
            Title      = title,
            FilePath   = filePath,
            DocType    = docType,
            Unsaved    = unsaved,
            BoundsMm   = boundsMm,
            MassGrams  = massGrams,
            VolumeMm3  = volumeMm3,
            Features   = features   ?? [],
            Components = components ?? [],
            Mates      = mates      ?? []
        };
        return s with { SnapshotToken = ComputeSnapshotToken(s) };
    }

    // ── JSON payload ──────────────────────────────────────────────────────────

    /// <summary>Serializes the full model state to compact JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    // ── Human-readable text output ────────────────────────────────────────────

    /// <summary>
    /// Formats the model state as human-readable text intended for LLM consumption.
    /// </summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        AppendHeader(sb);
        AppendGeometry(sb);
        sb.AppendLine();

        switch (DocType)
        {
            case "Part":
                AppendFeatureTree(sb);
                break;
            case "Assembly":
                AppendComponentTree(sb);
                sb.AppendLine();
                AppendMates(sb);
                break;
            case "Drawing":
                sb.AppendLine("Drawing document — feature tree not applicable.");
                break;
            default:
                sb.AppendLine($"Document type: {DocType}");
                break;
        }

        return sb.ToString().TrimEnd();
    }

    private void AppendHeader(StringBuilder sb)
    {
        var modTag = Unsaved ? ", unsaved" : string.Empty;
        sb.AppendLine($"Document: {Title}  ({DocType}{modTag})");
        if (!string.IsNullOrWhiteSpace(FilePath))
            sb.AppendLine($"Path:     {FilePath}");
    }

    private void AppendGeometry(StringBuilder sb)
    {
        if (BoundsMm is { Length: >= 3 })
            sb.AppendLine($"Bounds:   {BoundsMm[0]:F1} × {BoundsMm[1]:F1} × {BoundsMm[2]:F1} mm");

        if (MassGrams.HasValue || VolumeMm3.HasValue)
            sb.AppendLine($"Mass:     {MassGrams:F1} g  |  Volume: {VolumeMm3:F0} mm³");
    }

    private void AppendFeatureTree(StringBuilder sb)
    {
        if (Features.Count == 0)
        {
            sb.AppendLine("Feature Tree: (empty)");
            return;
        }

        sb.AppendLine($"Feature Tree ({Features.Count} features):");

        for (int i = 0; i < Features.Count; i++)
        {
            var f       = Features[i];
            var suppTag = f.Suppressed ? "  [suppressed]" : string.Empty;
            var dimTag  = f.Dimensions.Count > 0
                ? "  " + string.Join(", ", f.Dimensions.Select(d => $"{d.Name}={d.Value}"))
                : string.Empty;

            sb.AppendLine($"  [{i + 1,2}]  {f.Name,-28}  {f.TypeLabel,-20}{dimTag}{suppTag}");

            if (f.Sketch  is { } sk) AppendSketchDetail(sb, sk);
            if (f.Extrude is { } ex) AppendExtrudeDetail(sb, ex);
        }
    }

    private static void AppendSketchDetail(StringBuilder sb, SketchInfo sk)
    {
        var normalStr = sk.Normal is { Length: >= 3 } n
            ? $"({n[0]:F2}, {n[1]:F2}, {n[2]:F2})"
            : "?";
        var originStr = sk.Origin is { Length: >= 3 } o
            ? $"({o[0]:F1}, {o[1]:F1}, {o[2]:F1}) mm"
            : "?";

        sb.AppendLine(
            $"         Plane: {sk.PlaneName,-16}  Normal: {normalStr,-22}  Origin: {originStr}  {sk.ConstrainedStatus}");

        foreach (var group in sk.Segments.GroupBy(s => s.SegType).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"         {group.Key}s ({group.Count()}):");
            foreach (var seg in group)
            {
                var ctag = seg.IsConstruction ? " [c]" : "";
                switch (seg.SegType)
                {
                    case "Line"
                        when seg.Start is { Length: >= 3 } s && seg.End is { Length: >= 3 } e:
                        sb.AppendLine(
                            $"           ({s[0]:F2}, {s[1]:F2}, {s[2]:F2})" +
                            $" → ({e[0]:F2}, {e[1]:F2}, {e[2]:F2}) mm{ctag}");
                        break;

                    case "Circle"
                        when seg.Center is { Length: >= 3 } c:
                        sb.AppendLine(
                            $"           Center: ({c[0]:F2}, {c[1]:F2}, {c[2]:F2})  R: {seg.RadiusMm:F2} mm{ctag}");
                        break;

                    case "Arc"
                        when seg.Center is { Length: >= 3 } c:
                        var s2 = seg.Start is { Length: >= 3 } sp ? $"({sp[0]:F2}, {sp[1]:F2}, {sp[2]:F2})" : "?";
                        var e2 = seg.End   is { Length: >= 3 } ep ? $"({ep[0]:F2}, {ep[1]:F2}, {ep[2]:F2})" : "?";
                        sb.AppendLine(
                            $"           Center: ({c[0]:F2}, {c[1]:F2}, {c[2]:F2})  R: {seg.RadiusMm:F2} mm  {s2} → {e2} mm{ctag}");
                        break;

                    default:
                        sb.AppendLine($"           {seg.SegType}{ctag}");
                        break;
                }
            }
        }

        if (sk.Relations.Count > 0)
            sb.AppendLine($"         Relations: {string.Join(", ", sk.Relations)}");
    }

    private static void AppendExtrudeDetail(StringBuilder sb, ExtrudeInfo ex)
    {
        var draftStr = ex.DraftAngle1Deg > 0.001 ? $"  Draft: {ex.DraftAngle1Deg:F2}°" : "";
        var dirStr   = ex.Reversed ? "Reversed" : "Forward";
        sb.AppendLine($"         {ex.BossOrCut,-6}  D1: {ex.EndCondition1} {ex.Depth1Mm:F2} mm{draftStr}  Dir: {dirStr}");

        if (ex.BothDirections && ex.EndCondition2 is not null)
        {
            var draftStr2 = (ex.DraftAngle2Deg ?? 0) > 0.001 ? $"  Draft: {ex.DraftAngle2Deg:F2}°" : "";
            sb.AppendLine($"         D2: {ex.EndCondition2} {ex.Depth2Mm:F2} mm{draftStr2}");
        }
    }

    private void AppendComponentTree(StringBuilder sb)
    {
        if (Components.Count == 0) { sb.AppendLine("Components: (none)"); return; }

        sb.AppendLine($"Components ({Components.Count} top-level):");
        foreach (var c in Components)
            sb.AppendLine($"  {c.Name,-30}  {c.SourceFile,-30}  {c.State}");
    }

    private void AppendMates(StringBuilder sb)
    {
        if (Mates.Count == 0) { sb.AppendLine("Mates: (none)"); return; }

        sb.AppendLine($"Mates ({Mates.Count}):");
        foreach (var m in Mates)
        {
            var valStr  = m.Value.HasValue ? $" {m.Value.Value:F2}{(m.IsAngle ? "°" : "mm")}" : string.Empty;
            var flipStr = m.Flipped ? " [flipped]" : string.Empty;
            sb.AppendLine(
                $"  {m.Name,-22}  {m.TypeLabel + valStr,-24}  {m.Alignment,-14}  {m.Entity1} ↔ {m.Entity2}{flipStr}");
        }
    }

    // ── Snapshot token ────────────────────────────────────────────────────────

    private static string ComputeSnapshotToken(ModelState s)
    {
        var key = string.Concat(
            s.DocType, "|", s.Title, "|", s.FilePath ?? "",
            "|", s.Features.Count,
            "|", string.Join(",", s.Features.Select(f => f.Name)),
            "|", s.Components.Count,
            "|", string.Join(",", s.Components.Select(c => c.Name)),
            "|", s.Mates.Count);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hash[..8]);
    }
}

// ── Data records ──────────────────────────────────────────────────────────────

public sealed record FeatureInfo(
    [property: JsonPropertyName("name")]      string         Name,
    [property: JsonPropertyName("type")]      string         TypeLabel,
    [property: JsonPropertyName("suppressed")]bool           Suppressed,
    [property: JsonPropertyName("dims")]      List<DimInfo>  Dimensions,
    [property: JsonPropertyName("sketch")]    SketchInfo?    Sketch   = null,
    [property: JsonPropertyName("extrude")]   ExtrudeInfo?   Extrude  = null);

public sealed record DimInfo(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("value")] string Value);

public sealed record SketchInfo(
    [property: JsonPropertyName("plane")]  string                         PlaneName,
    [property: JsonPropertyName("normal")] double[]?                      Normal,
    [property: JsonPropertyName("origin")] double[]?                      Origin,
    [property: JsonPropertyName("status")] string                         ConstrainedStatus,
    [property: JsonPropertyName("segs")]   IReadOnlyList<SketchSegmentInfo> Segments,
    [property: JsonPropertyName("rels")]   IReadOnlyList<string>          Relations);

/// <summary>
/// A single sketch entity. Points are in millimetres in model (world) space.
/// <list type="bullet">
///   <item>Line  — Start, End</item>
///   <item>Arc   — Center, Start, End, RadiusMm</item>
///   <item>Circle— Center, RadiusMm</item>
///   <item>Ellipse — Center</item>
///   <item>Spline — (no point data; segment count only)</item>
/// </list>
/// </summary>
public sealed record SketchSegmentInfo(
    [property: JsonPropertyName("type")]   string    SegType,
    [property: JsonPropertyName("start")]  double[]? Start,
    [property: JsonPropertyName("end")]    double[]? End,
    [property: JsonPropertyName("center")] double[]? Center,
    [property: JsonPropertyName("r")]      double?   RadiusMm,
    [property: JsonPropertyName("c")]      bool      IsConstruction);

public sealed record ExtrudeInfo(
    [property: JsonPropertyName("kind")]  string  BossOrCut,
    [property: JsonPropertyName("ec1")]   string  EndCondition1,
    [property: JsonPropertyName("d1")]    double  Depth1Mm,
    [property: JsonPropertyName("dr1")]   double  DraftAngle1Deg,
    [property: JsonPropertyName("both")]  bool    BothDirections,
    [property: JsonPropertyName("rev")]   bool    Reversed,
    [property: JsonPropertyName("ec2")]   string? EndCondition2,
    [property: JsonPropertyName("d2")]    double? Depth2Mm,
    [property: JsonPropertyName("dr2")]   double? DraftAngle2Deg);

public sealed record ComponentInfo(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("file")]  string SourceFile,
    [property: JsonPropertyName("state")] string State);

public sealed record MateInfo(
    [property: JsonPropertyName("name")]    string  Name,
    [property: JsonPropertyName("type")]    string  TypeLabel,
    [property: JsonPropertyName("e1")]      string  Entity1,
    [property: JsonPropertyName("e2")]      string  Entity2,
    [property: JsonPropertyName("value")]   double? Value,
    [property: JsonPropertyName("isAngle")] bool    IsAngle,
    [property: JsonPropertyName("align")]   string  Alignment,
    [property: JsonPropertyName("flipped")] bool    Flipped);
