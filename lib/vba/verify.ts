import type { VBAVerifyResult } from "@/types/vba";

/**
 * Pure structural check for SolidWorks VBA macros.
 * No LLM calls — fast and deterministic.
 */
export function verifyVBAFormat(code: string): VBAVerifyResult {
  const trimmed = code.trim();

  if (!/^Sub\s+\w+\s*\(/im.test(trimmed)) {
    return { valid: false, error: "Missing Sub declaration" };
  }

  if (!/End\s+Sub\s*$/im.test(trimmed)) {
    return { valid: false, error: "Missing End Sub" };
  }

  if (!/swApp|swDoc|swModel/i.test(trimmed)) {
    return { valid: false, error: "No SolidWorks API reference found" };
  }

  if (/```/.test(trimmed)) {
    return { valid: false, error: "Response contains markdown artifacts" };
  }

  return { valid: true };
}
