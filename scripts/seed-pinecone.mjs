/**
 * One-time script to seed Pinecone with sample SolidWorks VBA snippets.
 * Run: node scripts/seed-pinecone.mjs
 * Requires: PINECONE_API_KEY, PINECONE_INDEX, OPENAI_API_KEY in .env.local
 */

import { readFileSync } from "fs";
import { Pinecone } from "@pinecone-database/pinecone";
import OpenAI from "openai";

// Load .env.local manually (no dotenv dependency needed)
const envFile = readFileSync(".env.local", "utf-8");
for (const line of envFile.split("\n")) {
  const [key, ...rest] = line.split("=");
  if (key && rest.length) process.env[key.trim()] = rest.join("=").trim();
}

const pinecone = new Pinecone({ apiKey: process.env.PINECONE_API_KEY });
const openai = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });

const NAMESPACE = "solidworks-vba";

const snippets = [
  {
    id: "fillet-sheet-metal-001",
    part_type: "sheet_metal",
    action: "add fillet to edges",
    description: "Add a fillet to all edges of a sheet metal part",
    vba_code: `Sub AddFilletSheetMetal()
    Dim swApp As Object
    Dim swDoc As Object
    Dim swFeat As Object
    Set swApp = Application.SldWorks
    Set swDoc = swApp.ActiveDoc
    swDoc.ClearSelection2 True
    swDoc.Extension.SelectAll
    Dim radius As Double
    radius = 0.003 ' 3mm in meters
    swDoc.FeatureManager.InsertFillet 0, True, False, False, False, radius, 0, 0, 0
    swDoc.EditRebuild3
End Sub`,
  },
  {
    id: "extrude-boss-001",
    part_type: "extrusion",
    action: "add boss extrude",
    description: "Create a boss extrude feature on a part",
    vba_code: `Sub AddBossExtrude()
    Dim swApp As Object
    Dim swDoc As Object
    Dim swSketch As Object
    Set swApp = Application.SldWorks
    Set swDoc = swApp.ActiveDoc
    swDoc.SketchManager.InsertSketch True
    swDoc.SketchManager.CreateCircle 0, 0, 0, 0.01, 0, 0
    swDoc.SketchManager.InsertSketch False
    Dim depth As Double
    depth = 0.02 ' 20mm
    swDoc.FeatureManager.FeatureExtrusion2 True, False, False, 0, 0, depth, 0, False, False, False, False, 0, 0, False, False, False, False, True, True, True, 0, 0, False
    swDoc.EditRebuild3
End Sub`,
  },
  {
    id: "chamfer-general-001",
    part_type: "general",
    action: "add chamfer to edges",
    description: "Add chamfer to selected edges",
    vba_code: `Sub AddChamfer()
    Dim swApp As Object
    Dim swDoc As Object
    Set swApp = Application.SldWorks
    Set swDoc = swApp.ActiveDoc
    swDoc.ClearSelection2 True
    Dim distance As Double
    distance = 0.002 ' 2mm
    swDoc.FeatureManager.InsertChamfer 0, True, distance, 0.785398, 0, 0, True, True
    swDoc.EditRebuild3
End Sub`,
  },
  {
    id: "hole-sheet-metal-001",
    part_type: "sheet_metal",
    action: "create circular hole",
    description: "Create a circular hole in a sheet metal part",
    vba_code: `Sub CreateHole()
    Dim swApp As Object
    Dim swDoc As Object
    Set swApp = Application.SldWorks
    Set swDoc = swApp.ActiveDoc
    swDoc.SketchManager.InsertSketch True
    Dim diameter As Double
    diameter = 0.005 ' 5mm radius = 2.5mm
    swDoc.SketchManager.CreateCircle 0, 0, 0, diameter / 2, 0, 0
    swDoc.SketchManager.InsertSketch False
    swDoc.FeatureManager.FeatureCut2 True, False, False, 0, 0, 0.01, 0, False, False, False, False, 0, 0, False, False, False, False, True, True, True, True, False, 0, 0, False
    swDoc.EditRebuild3
End Sub`,
  },
  {
    id: "mirror-extrusion-001",
    part_type: "extrusion",
    action: "mirror feature about plane",
    description: "Mirror a feature about the front plane",
    vba_code: `Sub MirrorFeature()
    Dim swApp As Object
    Dim swDoc As Object
    Set swApp = Application.SldWorks
    Set swDoc = swApp.ActiveDoc
    Dim swSelMgr As Object
    Set swSelMgr = swDoc.SelectionManager
    swDoc.Extension.SelectByID2 "Front Plane", "PLANE", 0, 0, 0, False, 2, Nothing, 0
    swDoc.FeatureManager.InsertMirrorFeature2 True, False, False, False, 0
    swDoc.EditRebuild3
End Sub`,
  },
];

async function embedText(text) {
  const response = await openai.embeddings.create({
    model: "text-embedding-3-small",
    input: text,
    dimensions: 1024,
  });
  return response.data[0].embedding;
}

async function main() {
  const index = pinecone.index(process.env.PINECONE_INDEX);
  const namespace = index.namespace(NAMESPACE);

  console.log(`Seeding ${snippets.length} VBA snippets into Pinecone namespace "${NAMESPACE}"...\n`);

  const vectors = [];
  for (const snippet of snippets) {
    const text = `${snippet.action} ${snippet.part_type} ${snippet.description}`;
    console.log(`Embedding: ${snippet.id}`);
    const vector = await embedText(text);
    vectors.push({
      id: snippet.id,
      values: vector,
      metadata: {
        part_type: snippet.part_type,
        action: snippet.action,
        description: snippet.description,
        vba_code: snippet.vba_code,
      },
    });
  }

  await namespace.upsert({ records: vectors });
  console.log(`\n✓ Seeded ${vectors.length} vectors into namespace "${NAMESPACE}"`);
  console.log("Pinecone is ready. Test with a query that matches one of these actions.");
}

main().catch(console.error);
