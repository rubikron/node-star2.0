import { ChatAnthropic } from "@langchain/anthropic";
import { SystemMessage, HumanMessage } from "@langchain/core/messages";
import { TavilySearchAPIRetriever } from "@langchain/community/retrievers/tavily_search_api";
import { queryVBASnippets } from "@/lib/pinecone/client";
import { verifyVBAFormat } from "@/lib/vba/verify";
import { VBA_SYSTEM_PROMPT, buildUserPrompt } from "@/lib/vba/prompts";
import { VBAState } from "./state";

type State = typeof VBAState.State;

// ---------------------------------------------------------------------------
// Node 1: query_pinecone
// Embedding + vector retrieval — no LLM.
// ---------------------------------------------------------------------------
export async function queryPineconeNode(state: State): Promise<Partial<State>> {
  const { score, vbaCode } = await queryVBASnippets(state.action);

  const hit = score >= 0.82;
  const vbaPreview = vbaCode ? vbaCode.slice(0, 100) : null;

  console.log(`[query_pinecone] Score: ${score.toFixed(3)} | Hit: ${hit}`);
  return {
    pinecone_vba: hit ? vbaCode : null,
    pinecone_score: score,
    debug_trace: {
      query_pinecone: { hit, score, vba_preview: vbaPreview },
    },
  };
}

// ---------------------------------------------------------------------------
// Node 2: web_search
// Tavily retrieval — no LLM.
// ---------------------------------------------------------------------------
export async function webSearchNode(state: State): Promise<Partial<State>> {
  const query = `SolidWorks VBA macro ${state.action} site:help.solidworks.com OR site:forum.solidworks.com OR site:stackoverflow.com`;

  const retriever = new TavilySearchAPIRetriever({
    k: 3,
    apiKey: process.env.TAVILY_API_KEY,
  });

  const docs = await retriever.invoke(query);
  const searchResults = docs.map((d) => d.pageContent).join("\n\n---\n\n");

  console.log(`[web_search] Found ${docs.length} results`);
  return {
    search_results: searchResults,
    debug_trace: {
      web_search: {
        query,
        results_count: docs.length,
        snippet_preview: searchResults.slice(0, 200),
      },
    },
  };
}

// ---------------------------------------------------------------------------
// Node 3: generate_vba
// Claude Sonnet — called after Pinecone hit, after web_search, or on retry.
// ---------------------------------------------------------------------------
export async function generateVBANode(state: State): Promise<Partial<State>> {
  const attempt = state.retry_count + 1;
  console.log(`[generate_vba] Attempt ${attempt}`);

  const model = new ChatAnthropic({
    model: "claude-sonnet-4-6",
    apiKey: process.env.ANTHROPIC_API_KEY,
  });

  const userPrompt = buildUserPrompt({
    part_id: state.part_id,
    action: state.action,
    pinecone_vba: state.pinecone_vba,
    search_results: state.search_results,
    retry_count: state.retry_count,
    verify_error: state.verify_error,
  });

  const response = await model.invoke([
    new SystemMessage(VBA_SYSTEM_PROMPT),
    new HumanMessage(userPrompt),
  ]);

  const rawVba =
    typeof response.content === "string"
      ? response.content
      : response.content
          .filter((block) => block.type === "text")
          .map((block) => (block as { type: "text"; text: string }).text)
          .join("");

  return {
    raw_vba: rawVba,
    debug_trace: {
      generate_vba: {
        attempt,
        raw_vba_preview: rawVba.slice(0, 150),
      },
    },
  };
}

// ---------------------------------------------------------------------------
// Node 4: verify_vba
// Pure TS structural check — no LLM.
// ---------------------------------------------------------------------------
export async function verifyVBANode(state: State): Promise<Partial<State>> {
  const result = verifyVBAFormat(state.raw_vba ?? "");

  console.log(`[verify_vba] Valid: ${result.valid}`);

  if (result.valid) {
    return {
      final_vba: state.raw_vba,
      debug_trace: {
        verify_vba: { valid: true, error: null, retry_count: state.retry_count },
      },
    };
  }

  return {
    verify_error: result.error ?? "Unknown verification error",
    retry_count: state.retry_count + 1,
    debug_trace: {
      verify_vba: {
        valid: false,
        error: result.error,
        retry_count: state.retry_count + 1,
      },
    },
  };
}
