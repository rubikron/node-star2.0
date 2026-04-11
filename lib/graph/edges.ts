import { END } from "@langchain/langgraph";
import { VBAState } from "./state";

type State = typeof VBAState.State;

/**
 * After check_part: short-circuit to END on error, otherwise proceed to query_pinecone.
 */
export function routeAfterPartCheck(state: State): string {
  return state.error_status ? END : "query_pinecone";
}

/**
 * After query_pinecone: go directly to generate_vba on a Pinecone hit (score ≥ 0.82),
 * otherwise fall through to web_search.
 */
export function routeAfterPinecone(state: State): string {
  return (state.pinecone_score ?? 0) >= 0.82 ? "generate_vba" : "web_search";
}

/**
 * After verify_vba:
 * - valid → END (200)
 * - invalid + retry_count < 2 → generate_vba (one retry allowed)
 *   Note: verify_vba already incremented retry_count on failure,
 *   so after the first failure retry_count is 1. We allow retry when < 2.
 * - invalid + retry_count >= 2 → END (422)
 */
export function routeAfterVerify(state: State): string {
  if (state.final_vba) return END;
  if (state.retry_count < 2) return "generate_vba";
  return END;
}
