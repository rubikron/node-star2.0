import { describe, it, expect } from "vitest";
import { END } from "@langchain/langgraph";
import { routeAfterPartCheck, routeAfterPinecone, routeAfterVerify } from "./edges";
import type { VBAState } from "./state";

type State = typeof VBAState.State;

const baseState: State = {
  part_id: "SW-001",
  action: "add fillet",
  debug: false,
  part_type: null,
  part_description: null,
  pinecone_vba: null,
  pinecone_score: null,
  search_results: null,
  raw_vba: null,
  retry_count: 0,
  verify_error: null,
  final_vba: null,
  error_status: null,
  error_message: null,
  debug_trace: {},
};

describe("routeAfterPartCheck", () => {
  it("routes to query_pinecone when no error", () => {
    expect(routeAfterPartCheck({ ...baseState, part_type: "sheet_metal" })).toBe("query_pinecone");
  });

  it("routes to END on error_status 404", () => {
    expect(routeAfterPartCheck({ ...baseState, error_status: 404 })).toBe(END);
  });
});

describe("routeAfterPinecone", () => {
  it("routes to generate_vba on score >= 0.82", () => {
    expect(routeAfterPinecone({ ...baseState, pinecone_score: 0.82 })).toBe("generate_vba");
    expect(routeAfterPinecone({ ...baseState, pinecone_score: 0.95 })).toBe("generate_vba");
  });

  it("routes to web_search on score < 0.82", () => {
    expect(routeAfterPinecone({ ...baseState, pinecone_score: 0.81 })).toBe("web_search");
    expect(routeAfterPinecone({ ...baseState, pinecone_score: 0 })).toBe("web_search");
  });

  it("routes to web_search when pinecone_score is null", () => {
    expect(routeAfterPinecone({ ...baseState, pinecone_score: null })).toBe("web_search");
  });
});

describe("routeAfterVerify", () => {
  it("routes to END when final_vba is set (success)", () => {
    expect(
      routeAfterVerify({ ...baseState, final_vba: "Sub Test()\nEnd Sub", retry_count: 0 })
    ).toBe(END);
  });

  it("routes to generate_vba for retry when retry_count is 1 (first failure)", () => {
    // verify_vba increments retry_count on failure: first failure sets it to 1
    expect(
      routeAfterVerify({ ...baseState, final_vba: null, retry_count: 1 })
    ).toBe("generate_vba");
  });

  it("routes to END when retry_count is 2 (exhausted retries)", () => {
    expect(
      routeAfterVerify({ ...baseState, final_vba: null, retry_count: 2 })
    ).toBe(END);
  });

  it("routes to END for retry_count >= 2", () => {
    expect(
      routeAfterVerify({ ...baseState, final_vba: null, retry_count: 3 })
    ).toBe(END);
  });
});
