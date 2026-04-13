"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.validate = validate;
const zod_1 = require("zod");
const anthropic_1 = require("@langchain/anthropic");
const messages_1 = require("@langchain/core/messages");
const traceable_1 = require("langsmith/traceable");
const config_1 = require("../../config");
const ValidatorOutputSchema = zod_1.z.object({
    is_valid: zod_1.z.boolean(),
    errors: zod_1.z.array(zod_1.z.string()),
    corrected_code: zod_1.z.string(),
});
const SYSTEM_PROMPT = "You are a SolidWorks VBA code reviewer. " +
    "Check if the VBA code correctly uses the API methods shown in the documentation. " +
    "If valid, set is_valid to true and corrected_code to empty string. " +
    "If invalid, set is_valid to false, list errors, and provide the corrected code.";
function deterministicChecks(vbaCode) {
    const errors = [];
    if (!vbaCode.includes("Sub Main")) {
        errors.push("Missing Sub Main");
    }
    if (!vbaCode.includes("End Sub")) {
        errors.push("Missing End Sub");
    }
    if (!vbaCode.includes("Option Explicit")) {
        errors.push("Missing Option Explicit");
    }
    if (!vbaCode.includes("SldWorks") && !vbaCode.includes("swApp")) {
        errors.push("Missing SldWorks application reference");
    }
    const dimCallPattern = /(CreateLine2|CreateArc2|AddDimension2|CreateCircle2)\([^)]*\)/g;
    const dimCalls = vbaCode.match(dimCallPattern) ?? [];
    for (const call of dimCalls) {
        const nums = call.match(/\b(\d+\.?\d*)\b/g) ?? [];
        if (nums.some((n) => parseFloat(n) > 1.0)) {
            errors.push(`Possible mm-not-meters error in: ${call.slice(0, 40)}`);
            break;
        }
    }
    return errors;
}
const tracedValidate = (0, traceable_1.traceable)(async (input) => {
    const llm = new anthropic_1.ChatAnthropic({
        model: "claude-sonnet-4-5",
        apiKey: config_1.config.ANTHROPIC_API_KEY,
    });
    // withStructuredOutput generic inference can hit TS depth limits;
    // cast via any to avoid the excessively-deep instantiation error.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const structured = llm.withStructuredOutput(ValidatorOutputSchema);
    const userContent = `VBA Code to review:\n\`\`\`vba\n${input.vba_code}\n\`\`\`` +
        (input.api_docs.length > 0
            ? `\n\n## API Documentation\n${input.api_docs.join("\n\n")}`
            : "");
    const result = await structured.invoke([
        new messages_1.SystemMessage(SYSTEM_PROMPT),
        new messages_1.HumanMessage(userContent),
    ]);
    return result;
}, { name: "validator", run_type: "llm" });
async function validate(state) {
    const deterministicErrors = deterministicChecks(state.vba_code);
    const llmResult = await tracedValidate({
        vba_code: state.vba_code,
        api_docs: state.api_docs,
    });
    const allErrors = [...deterministicErrors, ...llmResult.errors];
    const is_valid = deterministicErrors.length === 0 && llmResult.is_valid;
    const validation = {
        is_valid,
        errors: allErrors,
        corrected_code: llmResult.corrected_code,
    };
    return { validation };
}
//# sourceMappingURL=validator.js.map