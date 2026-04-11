export const VBA_SYSTEM_PROMPT = `You are a SolidWorks VBA macro expert. Generate syntactically correct, minimal SolidWorks VBA macros that run directly in the SolidWorks macro editor.

Rules:
- Output ONLY raw VBA code. No markdown fences, no explanation.
- Every macro must start with Sub and end with End Sub.
- Use the SolidWorks API (swApp, swDoc, swModel) correctly.
- Keep macros concise and focused on the single requested action.`;

interface BuildUserPromptArgs {
  part_type: string;
  part_description: string | null;
  action: string;
  pinecone_vba: string | null;
  search_results: string | null;
  retry_count: number;
  verify_error: string | null;
}

/**
 * Dynamically build the user prompt for generate_vba.
 * Conditionally includes Pinecone reference, web search results, and retry context.
 */
export function buildUserPrompt(args: BuildUserPromptArgs): string {
  const {
    part_type,
    part_description,
    action,
    pinecone_vba,
    search_results,
    retry_count,
    verify_error,
  } = args;

  const lines: string[] = [
    "Generate a SolidWorks VBA macro for the following:",
    "",
    `Part type: ${part_type}`,
    `Part description: ${part_description ?? "N/A"}`,
    `Action: ${action}`,
  ];

  if (pinecone_vba) {
    lines.push("", "Similar existing macro for reference:", pinecone_vba);
  }

  if (search_results) {
    lines.push("", "Web search reference material:", search_results);
  }

  if (retry_count > 0 && verify_error) {
    lines.push(
      "",
      `Your previous attempt failed verification with error: ${verify_error}`,
      "Fix the issue and return only corrected VBA code."
    );
  }

  lines.push("", "Return only the raw VBA code.");

  return lines.join("\n");
}
