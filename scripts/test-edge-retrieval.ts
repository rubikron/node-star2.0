import "../src/config";
import { Pinecone } from "@pinecone-database/pinecone";
import { config } from "../src/config";

const pc: any = new Pinecone({ apiKey: config.PINECONE_API_KEY });

async function test() {
  const index: any = pc.index(config.PINECONE_INDEX).namespace("Default");

  const queries = [
    "GetFaces GetEdges Select4 edge traversal fillet",
    "InsertFeatureFillet radius parameters",
    "FeatureExtrusion3 parameters end condition depth solid",
    "select all edges of a feature for fillet",
  ];

  for (const q of queries) {
    const emb: any = await pc.inference.embed("llama-text-embed-v2", [q], {
      inputType: "query",
      truncate: "END",
    });
    const vec = emb?.[0]?.values ?? emb?.data?.[0]?.values;
    const res = await index.query({ vector: vec, topK: 3, includeMetadata: false });
    console.log(`\nQuery: "${q}"`);
    res.matches?.forEach((m: any) =>
      console.log(`  ${m.score.toFixed(3)}  ${m.id}`)
    );
  }
}

test().catch(console.error);
