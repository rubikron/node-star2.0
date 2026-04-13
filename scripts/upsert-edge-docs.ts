import "../src/config";
import { Pinecone } from "@pinecone-database/pinecone";
import { config } from "../src/config";

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const pc: any = new Pinecone({ apiKey: config.PINECONE_API_KEY });

const DOCS = [
  {
    id: "edge-traversal-getfaces-getedges",
    text: `## Programmatic Edge Traversal for Fillet and Chamfer

To select all edges of a feature (e.g. all 12 edges of a cube) use geometry traversal, NOT coordinate-based SelectByID2.

Method signatures:
- IFeature::GetFaces() As Variant — returns array of SldWorks.Face2 objects belonging to the feature
- IFace2::GetEdges() As Variant — returns array of SldWorks.Edge objects on a face
- IEdge::Select4(Append As Boolean, Mark As SldWorks.SelectData) As Boolean
  - Append = True to add to existing selection, False to replace
  - Mark = Nothing uses default mark value 1 (correct for fillet/chamfer edge selection)

IMPORTANT: A cube has 12 edges but 24 face-edge pairs (each edge borders 2 faces). You MUST deduplicate using Scripting.Dictionary + ObjPtr to avoid double-selecting edges.

Pattern to select all edges of an extruded feature (with deduplication):
  Dim selectedEdges As Object
  Set selectedEdges = CreateObject("Scripting.Dictionary")
  Dim edgeKey As String
  swModel.ClearSelection2 True
  vFaces = swFeat.GetFaces()
  For i = 0 To UBound(vFaces)
    Set swFace = vFaces(i)
    vEdges = swFace.GetEdges()
    For j = 0 To UBound(vEdges)
      Set swEdge = vEdges(j)
      edgeKey = CStr(ObjPtr(swEdge))
      If Not selectedEdges.Exists(edgeKey) Then
        swEdge.Select4 True, Nothing
        selectedEdges.Add edgeKey, True
      End If
    Next j
  Next i
  Then call InsertFeatureFillet or InsertFeatureChamfer

This is the ONLY correct way to select all unique edges. Do NOT rely on Select4 deduplication.`,
  },
  {
    id: "insert-feature-fillet-full",
    text: `## InsertFeatureFillet — Full Parameter Reference

Function InsertFeatureFillet(
    FtypIn      As Long,     ' Fillet type: 4 = constant size (swFeatureFilletType_Constant)
    Radiusin    As Double,   ' Radius in meters (e.g. 0.003 for 3mm)
    Propagtype  As Long,     ' Overflow type: 1 = keep features
    OptIn       As Long,     ' Options: 1 = tangent propagation
    setbackdist As Long,     ' SMSmoothType: 0
    RadiusType  As Long,     ' 0 = not used
    RadiusList  As Long,     ' 0 = not used
    SetbackType As Long,     ' 0 = not used
    SetbackList As Long,     ' 0 = not used
    PointRadius As Long,     ' 0 = not used
    Param11     As Long,     ' 0
    Param12     As Long,     ' 0
    Param13     As Long      ' 0
) As Object

Returns: Object — cast to SldWorks.Feature. Returns Nothing on failure.

Edges must be pre-selected with mark = 1 before calling this function.
Use swFeat.GetFaces / face.GetEdges / edge.Select4(True, Nothing) to select all edges.

Example for 3mm constant fillet on all edges:
  Set swFillet = swFeatMgr.InsertFeatureFillet(4, 0.003, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0)
  If swFillet Is Nothing Then MsgBox "Fillet failed": Exit Sub`,
  },
  {
    id: "feature-extrusion3-full",
    text: `## FeatureExtrusion3 — Correct Signature (18 parameters exactly)

instance.FeatureExtrusion3(Sd, Flip, Dir, T1, T2, D1, D2, Dchk1, Dchk2, Ddir1, Ddir2, Dang1, Dang2, OffsetReverse1, OffsetReverse2, TranslateSurface1, TranslateSurface2, Merge)

Parameters:
1.  Sd               As Boolean  — True = single direction
2.  Flip             As Boolean  — False = normal direction
3.  Dir              As Boolean  — False = no reversal
4.  T1               As Long     — End condition Dir1: 0 = Blind
5.  T2               As Long     — End condition Dir2: 0 = Blind
6.  D1               As Double   — Depth Dir1 in meters (e.g. 0.05 for 50mm)
7.  D2               As Double   — Depth Dir2 in meters: 0 if single direction
8.  Dchk1            As Boolean  — False = no draft Dir1
9.  Dchk2            As Boolean  — False = no draft Dir2
10. Ddir1            As Boolean  — False = draft outward Dir1
11. Ddir2            As Boolean  — False = draft outward Dir2
12. Dang1            As Double   — Draft angle Dir1 in radians: 0
13. Dang2            As Double   — Draft angle Dir2 in radians: 0
14. OffsetReverse1   As Boolean  — False
15. OffsetReverse2   As Boolean  — False
16. TranslateSurface1 As Boolean — False
17. TranslateSurface2 As Boolean — False
18. Merge            As Boolean  — True = merge result into existing body

Returns: SldWorks.Feature. Returns Nothing on failure — always null-check.

Typical 50mm blind solid extrusion:
  Set swFeat = swFeatMgr.FeatureExtrusion3(True, False, False, 0, 0, 0.05, 0, False, False, False, False, 0, 0, False, False, False, False, True)
  If swFeat Is Nothing Then MsgBox "Extrusion failed": Exit Sub`,
  },
];

async function upsert() {
  const index: any = pc.index(config.PINECONE_INDEX).namespace("Default");

  console.log(`Upserting ${DOCS.length} documentation chunks...`);

  for (const doc of DOCS) {
    console.log(`\nEmbedding: ${doc.id}`);

    const embedded: any = await pc.inference.embed(
      "llama-text-embed-v2",
      [doc.text],
      { inputType: "passage", truncate: "END" }
    );

    const vector: number[] =
      embedded?.[0]?.values ?? embedded?.data?.[0]?.values;

    if (!vector) {
      console.error(`  Failed to embed ${doc.id}`);
      continue;
    }

    await index.upsert([
      {
        id: doc.id,
        values: vector,
        metadata: { text: doc.text, source: "manual-curated" },
      },
    ]);

    console.log(`  Upserted ${doc.id} (${vector.length}-dim)`);
  }

  console.log("\nDone. Verifying...");
  const stats = await index.describeIndexStats();
  console.log(`Total records: ${stats.totalRecordCount}`);
}

upsert().catch(console.error);
