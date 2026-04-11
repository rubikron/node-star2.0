import { describe, it, expect } from "vitest";
import { verifyVBAFormat } from "./verify";

describe("verifyVBAFormat", () => {
  const validMacro = `Sub AddFillet()
    Dim swApp As Object
    Dim swDoc As Object
    Set swApp = Application.SldWorks
    Set swDoc = swApp.ActiveDoc
    ' Add fillet logic
End Sub`;

  it("passes a valid VBA macro", () => {
    expect(verifyVBAFormat(validMacro)).toEqual({ valid: true });
  });

  it("fails when Sub declaration is missing", () => {
    const code = `Dim swApp As Object\nEnd Sub`;
    const result = verifyVBAFormat(code);
    expect(result.valid).toBe(false);
    expect(result.error).toMatch(/Missing Sub declaration/);
  });

  it("fails when End Sub is missing", () => {
    const code = `Sub AddFillet()\n    Dim swApp As Object\n    Set swApp = Application.SldWorks`;
    const result = verifyVBAFormat(code);
    expect(result.valid).toBe(false);
    expect(result.error).toMatch(/Missing End Sub/);
  });

  it("fails when no SolidWorks API reference", () => {
    const code = `Sub AddFillet()\n    Dim x As Integer\n    x = 5\nEnd Sub`;
    const result = verifyVBAFormat(code);
    expect(result.valid).toBe(false);
    expect(result.error).toMatch(/No SolidWorks API reference/);
  });

  it("fails when response contains markdown fences", () => {
    const code = "```vba\n" + validMacro + "\n```";
    const result = verifyVBAFormat(code);
    expect(result.valid).toBe(false);
    expect(result.error).toMatch(/markdown artifacts/);
  });

  it("accepts swDoc as SolidWorks API reference", () => {
    const code = `Sub Test()\n    Dim swDoc As Object\nEnd Sub`;
    expect(verifyVBAFormat(code)).toEqual({ valid: true });
  });

  it("accepts swModel as SolidWorks API reference", () => {
    const code = `Sub Test()\n    Dim swModel As Object\nEnd Sub`;
    expect(verifyVBAFormat(code)).toEqual({ valid: true });
  });

  it("is case-insensitive for Sub keyword", () => {
    const code = `sub addFillet()\n    Dim swApp As Object\nEnd Sub`;
    expect(verifyVBAFormat(code).valid).toBe(true);
  });
});
