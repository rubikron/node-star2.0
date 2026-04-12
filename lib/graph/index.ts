import { StateGraph, START, END } from "@langchain/langgraph";
import { VBAState } from "./state";
import {
  queryPineconeNode,
  webSearchNode,
  generateVBANode,
  verifyVBANode,
} from "./nodes";
import { routeAfterPinecone, routeAfterVerify } from "./edges";

export const graph = new StateGraph(VBAState)
  .addNode("query_pinecone", queryPineconeNode)
  .addNode("web_search", webSearchNode)
  .addNode("generate_vba", generateVBANode)
  .addNode("verify_vba", verifyVBANode)
  .addEdge(START, "query_pinecone")
  .addConditionalEdges("query_pinecone", routeAfterPinecone, {
    generate_vba: "generate_vba",
    web_search: "web_search",
  })
  .addEdge("web_search", "generate_vba")
  .addEdge("generate_vba", "verify_vba")
  .addConditionalEdges("verify_vba", routeAfterVerify, {
    generate_vba: "generate_vba",
    [END]: END,
  })
  .compile();
