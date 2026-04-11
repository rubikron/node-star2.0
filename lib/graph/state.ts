import { Annotation } from "@langchain/langgraph";

export const VBAState = Annotation.Root({
  // Inputs
  part_id: Annotation<string>(),
  action: Annotation<string>(),
  debug: Annotation<boolean>(),

  // Populated by check_part
  part_type: Annotation<string | null>(),
  part_description: Annotation<string | null>(),

  // Populated by query_pinecone
  pinecone_vba: Annotation<string | null>(),
  pinecone_score: Annotation<number | null>(),

  // Populated by web_search
  search_results: Annotation<string | null>(),

  // Populated by generate_vba + verify_vba
  raw_vba: Annotation<string | null>(),
  retry_count: Annotation<number>(),
  verify_error: Annotation<string | null>(),
  final_vba: Annotation<string | null>(),

  // Error handling
  error_status: Annotation<number | null>(),
  error_message: Annotation<string | null>(),

  // Debug trace — each node appends its output
  debug_trace: Annotation<Record<string, unknown>>({
    reducer: (a, b) => ({ ...a, ...b }),
    default: () => ({}),
  }),
});
