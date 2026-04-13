import "../src/config";
import { Pinecone } from "@pinecone-database/pinecone";
import { config } from "../src/config";

const EMBED_MODEL = "multilingual-e5-large";

async function embedQuery(pc: Pinecone, text: string): Promise<number[]> {
  const result = await pc.inference.embed(EMBED_MODEL, [text], {
    inputType: "query",
    truncate: "END",
  });
  const values = (result as any)?.[0]?.values ?? (result as any)?.data?.[0]?.values;
  if (!values) throw new Error("No embedding returned from inference API");
  return values;
}

async function testRetrieval() {
  const pc = new Pinecone({ apiKey: config.PINECONE_API_KEY });
  const index = pc.index(config.PINECONE_INDEX);

  const queries = [
    "extrude: Boss/base extrusion from sketch",
    "FeatureExtrusion2 method signature parameters",
    "extrude solid feature from 2D sketch",
  ];

  for (const q of queries) {
    console.log(`\n${"=".repeat(60)}`);
    console.log(`QUERY: "${q}"`);
    console.log("=".repeat(60));

    try {
      const vector = await embedQuery(pc, q);
      console.log(`Embedded: ${vector.length}-dim vector`);

      const results = await index.query({
        vector,
        topK: 3,
        includeMetadata: true,
      });

      console.log(`Hits: ${results.matches.length}`);
      results.matches.forEach((match, i) => {
        const text =
          (match.metadata?.["text"] as string) ??
          (match.metadata?.["content"] as string) ??
          (match.metadata?.["chunk"] as string) ??
          "(no text field in metadata)";
        console.log(`\n[${i + 1}] id: ${match.id} | score: ${match.score?.toFixed(4)}`);
        console.log(`metadata keys: ${Object.keys(match.metadata ?? {}).join(", ")}`);
        console.log(text.slice(0, 400));
      });
    } catch (err) {
      console.error("Query failed:", err);
    }
  }
}

testRetrieval().catch(console.error);
