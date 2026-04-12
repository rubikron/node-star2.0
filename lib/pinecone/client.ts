import { Pinecone } from "@pinecone-database/pinecone";
import { OpenAIEmbeddings } from "@langchain/openai";
import type { PineconeVBAMetadata } from "@/types/vba";

export interface PineconeQueryResult {
  score: number;
  vbaCode: string | null;
}

// Lazy singletons — initialized on first call so Next.js build doesn't throw
let _pinecone: Pinecone | null = null;
let _embeddings: OpenAIEmbeddings | null = null;

function getPineconeIndex() {
  if (!_pinecone) {
    if (!process.env.PINECONE_API_KEY) {
      throw new Error("PINECONE_API_KEY environment variable is required");
    }
    _pinecone = new Pinecone({ apiKey: process.env.PINECONE_API_KEY });
  }
  if (!process.env.PINECONE_INDEX) {
    throw new Error("PINECONE_INDEX environment variable is required");
  }
  return _pinecone.index(process.env.PINECONE_INDEX);
}

function getEmbeddings(): OpenAIEmbeddings {
  if (!_embeddings) {
    _embeddings = new OpenAIEmbeddings({
      model: "text-embedding-3-small",
      dimensions: 1024,
      apiKey: process.env.OPENAI_API_KEY,
    });
  }
  return _embeddings;
}

/**
 * Embed the action string and query Pinecone for similar VBA snippets.
 * Returns the top match's score and vba_code.
 */
export async function queryVBASnippets(
  action: string
): Promise<PineconeQueryResult> {
  const embeddings = getEmbeddings();
  const index = getPineconeIndex();

  const [vector] = await embeddings.embedDocuments([action]);

  const namespace = index.namespace("solidworks-vba");
  const result = await namespace.query({
    vector,
    topK: 3,
    includeMetadata: true,
  });

  const matches = result.matches ?? [];
  if (matches.length === 0) {
    return { score: 0, vbaCode: null };
  }

  const top = matches[0];
  const score = top.score ?? 0;
  const metadata = top.metadata as PineconeVBAMetadata | undefined;
  const vbaCode = metadata?.vba_code ?? null;

  return { score, vbaCode };
}
