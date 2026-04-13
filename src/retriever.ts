/* eslint-disable @typescript-eslint/no-explicit-any */
import { Pinecone } from "@pinecone-database/pinecone";
import { config } from "./config";

const pc: any = new Pinecone({ apiKey: config.PINECONE_API_KEY });
const index: any = pc.index(config.PINECONE_INDEX).namespace("Default");

const OPERATION_QUERIES: Record<string, string[]> = {
  sketch:  ["InsertSketch2 CreateCenterRectangle CreateCircle SketchManager"],
  extrude: ["FeatureExtrusion3 parameters end condition depth solid"],
  cut:     ["FeatureCut extruded cut parameters through blind"],
  fillet:  ["InsertFeatureFillet radius parameters", "GetFaces GetEdges Select4 edge traversal"],
  chamfer: ["InsertFeatureChamfer parameters distance angle"],
  shell:   ["InsertFeatureShell parameters wall thickness"],
  revolve: ["FeatureRevolve2 parameters axis angle"],
  pattern: ["FeatureLinearPattern FeatureCircularPattern parameters"],
  hole:    ["Hole Wizard FeatureHoleWizard parameters"],
};

const KEYWORD_RULES: [RegExp, string][] = [
  [/\b(cube|box|block|extrude|extrusion|rectangular|plate|bar|rod|solid|boss)\b/i, "extrude"],
  [/\b(cylinder|tube|pipe|disk|disc)\b/i, "extrude"],
  [/\bfillets?\b|\bounds?\b|\bblends?\b/i, "fillet"],
  [/\bchamfers?\b|\bbevels?\b/i, "chamfer"],
  [/\b(hole|drill|bore|pocket|slot|cut|through)\b/i, "cut"],
  [/\b(shell|hollow|thin.wall)\b/i, "shell"],
  [/\b(revolve|revolution|spin|lathe)\b/i, "revolve"],
  [/\b(pattern|array|mirror|repeat|linear|circular)\b/i, "pattern"],
  [/\b(hole wizard|counterbore|countersink|tapped)\b/i, "hole"],
];

async function queryPinecone(queryText: string): Promise<string[]> {
  try {
    const embedded: any = await pc.inference.embed(
      "llama-text-embed-v2",
      [queryText],
      { inputType: "query", truncate: "END" }
    );
    const vector: number[] =
      embedded?.[0]?.values ?? embedded?.data?.[0]?.values;
    if (!vector) return [];

    const results: any = await index.query({
      vector,
      topK: 3,
      includeMetadata: true,
    });

    return (results.matches as any[])
      .filter((m: any) => (m.score ?? 0) >= 0.3)
      .map(
        (m: any): string =>
          (m.metadata?.["text"] as string) ??
          (m.metadata?.["content"] as string) ??
          (m.metadata?.["chunk"] as string) ??
          ""
      )
      .filter(Boolean);
  } catch {
    return [];
  }
}

export async function retrieveDocsForQueries(queries: string[]): Promise<string[]> {
  const perFunction = await retrieveDocsPerFunction(queries);
  const seen = new Set<string>();
  const docs: string[] = [];
  for (const chunks of perFunction.values()) {
    for (const chunk of chunks) {
      if (!seen.has(chunk)) { seen.add(chunk); docs.push(chunk); }
    }
  }
  console.log(`[retriever] Retrieved ${docs.length} doc chunks`);
  return docs;
}

/** Returns a map of query → doc chunks. Empty array = not found in Pinecone. */
export async function retrieveDocsPerFunction(
  queries: string[]
): Promise<Map<string, string[]>> {
  console.log(`[retriever] Querying Pinecone for: ${queries.join(" | ")}`);
  const entries = await Promise.all(
    queries.map(async (q) => [q, await queryPinecone(q)] as [string, string[]])
  );
  return new Map(entries);
}

export async function retrieveDocs(request: string): Promise<string[]> {
  const detected = new Set<string>(["sketch"]);
  for (const [regex, op] of KEYWORD_RULES) {
    if (regex.test(request)) detected.add(op);
  }

  console.log(`[retriever] Detected: ${[...detected].join(", ")}`);

  const queries = new Set<string>();
  for (const op of detected) {
    for (const q of OPERATION_QUERIES[op] ?? []) queries.add(q);
  }

  return retrieveDocsForQueries([...queries]);
}
