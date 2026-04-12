import { describe, it, expect } from "vitest";
import { buildUserPrompt, VBA_SYSTEM_PROMPT } from "./prompts";

describe("VBA_SYSTEM_PROMPT", () => {
  it("contains key instruction elements", () => {
    expect(VBA_SYSTEM_PROMPT).toContain("autonomous design agent");
    expect(VBA_SYSTEM_PROMPT).toContain("Sub");
    expect(VBA_SYSTEM_PROMPT).toContain("End Sub");
    expect(VBA_SYSTEM_PROMPT).toContain("swApp");
  });
});

describe("buildUserPrompt", () => {
  const base = {
    part_id: "SW-001",
    action: "add a 3mm fillet to all edges",
    pinecone_vba: null,
    search_results: null,
    retry_count: 0,
    verify_error: null,
  };

  it("includes part_id and action", () => {
    const prompt = buildUserPrompt(base);
    expect(prompt).toContain("SW-001");
    expect(prompt).toContain("add a 3mm fillet to all edges");
  });

  it("includes Pinecone VBA when provided", () => {
    const prompt = buildUserPrompt({ ...base, pinecone_vba: "Sub ExistingMacro()\nEnd Sub" });
    expect(prompt).toContain("Similar existing macro for reference");
    expect(prompt).toContain("Sub ExistingMacro");
  });

  it("omits Pinecone section when null", () => {
    const prompt = buildUserPrompt(base);
    expect(prompt).not.toContain("Similar existing macro");
  });

  it("includes search results when provided", () => {
    const prompt = buildUserPrompt({ ...base, search_results: "Fillet API docs..." });
    expect(prompt).toContain("Web search reference material");
    expect(prompt).toContain("Fillet API docs");
  });

  it("omits search results section when null", () => {
    const prompt = buildUserPrompt(base);
    expect(prompt).not.toContain("Web search reference material");
  });

  it("includes retry context when retry_count > 0", () => {
    const prompt = buildUserPrompt({
      ...base,
      retry_count: 1,
      verify_error: "Missing End Sub",
    });
    expect(prompt).toContain("previous attempt failed verification");
    expect(prompt).toContain("Missing End Sub");
  });

  it("omits retry context on first attempt", () => {
    const prompt = buildUserPrompt(base);
    expect(prompt).not.toContain("previous attempt");
  });

  it("includes all sections when fully populated", () => {
    const prompt = buildUserPrompt({
      part_id: "SW-002",
      action: "add boss-extrude",
      pinecone_vba: "Sub Ref()\nEnd Sub",
      search_results: "Boss Extrude API...",
      retry_count: 1,
      verify_error: "Missing End Sub",
    });
    expect(prompt).toContain("Similar existing macro");
    expect(prompt).toContain("Web search reference material");
    expect(prompt).toContain("previous attempt failed verification");
  });
});
