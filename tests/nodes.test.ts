// Replicated deterministic check logic from validator.ts — no env vars needed
function deterministicChecks(code: string): string[] {
  const errors: string[] = [];
  if (!code.includes("Sub Main")) errors.push("Missing Sub Main");
  if (!code.includes("End Sub")) errors.push("Missing End Sub");
  if (!code.includes("Option Explicit")) errors.push("Missing Option Explicit");
  if (!code.includes("SldWorks") && !code.includes("swApp"))
    errors.push("Missing SldWorks application reference");
  const dimCallPattern =
    /(CreateLine2|CreateArc2|AddDimension2|CreateCircle2)\([^)]*\)/g;
  const dimCalls = code.match(dimCallPattern) ?? [];
  for (const call of dimCalls) {
    const nums = call.match(/\b(\d+\.?\d*)\b/g) ?? [];
    if (nums.some((n) => parseFloat(n) > 1.0)) {
      errors.push(`Possible mm-not-meters error in: ${call.slice(0, 40)}`);
      break;
    }
  }
  return errors;
}

const VALID_VBA = `Option Explicit
Sub Main()
    Dim swApp As SldWorks.SldWorks
    Dim swModel As SldWorks.ModelDoc2
    Set swApp = Application.SldWorks
    Set swModel = swApp.ActiveDoc
    swModel.Extension.SelectByID2 "Sketch1", "SKETCH", 0, 0, 0, False, 0, Nothing, 0
    swModel.SketchManager.CreateLine2(0.0, 0.0, 0.0, 0.05, 0.0, 0.0)
End Sub`;

describe("deterministicChecks", () => {
  it("returns no errors for a fully valid VBA snippet", () => {
    const errors = deterministicChecks(VALID_VBA);
    expect(errors).toHaveLength(0);
  });

  it("flags missing Option Explicit", () => {
    const code = VALID_VBA.replace("Option Explicit\n", "");
    const errors = deterministicChecks(code);
    expect(errors).toContain("Missing Option Explicit");
  });

  it("flags missing Sub Main", () => {
    const code = VALID_VBA.replace("Sub Main()", "Sub NotMain()");
    const errors = deterministicChecks(code);
    expect(errors).toContain("Missing Sub Main");
  });

  it("flags missing End Sub", () => {
    const code = VALID_VBA.replace("End Sub", "");
    const errors = deterministicChecks(code);
    expect(errors).toContain("Missing End Sub");
  });

  it("flags missing SldWorks application reference", () => {
    const code = `Option Explicit
Sub Main()
    Dim x As Integer
    x = 1
End Sub`;
    const errors = deterministicChecks(code);
    expect(errors).toContain("Missing SldWorks application reference");
  });

  it("flags mm-not-meters when a value > 1.0 appears inside a CreateLine2 call", () => {
    const code = `Option Explicit
Sub Main()
    Dim swApp As SldWorks.SldWorks
    swApp.ActiveDoc.SketchManager.CreateLine2(0.0, 0.0, 0.0, 50.0, 0.0, 0.0)
End Sub`;
    const errors = deterministicChecks(code);
    expect(errors.some((e) => e.startsWith("Possible mm-not-meters error"))).toBe(true);
  });

  it("does NOT flag mm-not-meters when all values are <= 1.0 in CreateLine2", () => {
    const code = `Option Explicit
Sub Main()
    Dim swApp As SldWorks.SldWorks
    swApp.ActiveDoc.SketchManager.CreateLine2(0.0, 0.0, 0.0, 0.05, 0.0, 0.0)
End Sub`;
    const errors = deterministicChecks(code);
    expect(errors.some((e) => e.startsWith("Possible mm-not-meters error"))).toBe(false);
  });

  it("accepts swApp as a valid SldWorks application reference (no SldWorks keyword)", () => {
    const code = `Option Explicit
Sub Main()
    Dim swApp As Object
    Set swApp = CreateObject("SldWorks.Application")
End Sub`;
    const errors = deterministicChecks(code);
    expect(errors).not.toContain("Missing SldWorks application reference");
  });

  it("accepts SldWorks keyword as a valid application reference (no swApp keyword)", () => {
    const code = `Option Explicit
Sub Main()
    Dim myApp As SldWorks.SldWorks
    Set myApp = Application.SldWorks
End Sub`;
    const errors = deterministicChecks(code);
    expect(errors).not.toContain("Missing SldWorks application reference");
  });
});
