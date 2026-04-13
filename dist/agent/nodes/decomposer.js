"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.decompose = decompose;
const zod_1 = require("zod");
const anthropic_1 = require("@langchain/anthropic");
const messages_1 = require("@langchain/core/messages");
const traceable_1 = require("langsmith/traceable");
const config_1 = require("../../config");
const StepSchema = zod_1.z.object({
    step_number: zod_1.z.number(),
    operation: zod_1.z.enum([
        "sketch",
        "extrude",
        "cut",
        "revolve",
        "fillet",
        "chamfer",
        "shell",
        "pattern",
        "hole",
        "mate",
    ]),
    description: zod_1.z.string(),
    parameters: zod_1.z.record(zod_1.z.unknown()),
});
const DecomposerOutputSchema = zod_1.z.object({ steps: zod_1.z.array(StepSchema) });
const SYSTEM_PROMPT = "You are a SolidWorks CAD expert. Break the user's request into ordered atomic CAD operations. " +
    "Valid operations: sketch, extrude, cut, revolve, fillet, chamfer, shell, pattern, hole, mate. " +
    "Extract all dimensions in millimeters. " +
    "Preserve correct feature order: base features first, detail features after, fillets last.";
const tracedDecompose = (0, traceable_1.traceable)(async (userContent) => {
    const llm = new anthropic_1.ChatAnthropic({
        model: "claude-sonnet-4-5",
        apiKey: config_1.config.ANTHROPIC_API_KEY,
    });
    // withStructuredOutput generic inference can hit TS depth limits with
    // complex Zod schemas; cast the result to the inferred output type.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const structured = llm.withStructuredOutput(DecomposerOutputSchema);
    const result = await structured.invoke([
        new messages_1.SystemMessage(SYSTEM_PROMPT),
        new messages_1.HumanMessage(userContent),
    ]);
    return result;
}, { name: "decomposer", run_type: "llm" });
async function decompose(state) {
    const lastHuman = [...state.messages]
        .reverse()
        .find((m) => m._getType() === "human");
    const userContent = lastHuman != null
        ? typeof lastHuman.content === "string"
            ? lastHuman.content
            : JSON.stringify(lastHuman.content)
        : "No input provided";
    const result = await tracedDecompose(userContent);
    return { steps: result.steps };
}
//# sourceMappingURL=decomposer.js.map