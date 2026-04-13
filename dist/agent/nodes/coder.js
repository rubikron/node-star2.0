"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.code = code;
const anthropic_1 = require("@langchain/anthropic");
const messages_1 = require("@langchain/core/messages");
const traceable_1 = require("langsmith/traceable");
const config_1 = require("../../config");
const SYSTEM_PROMPT = `You are an expert SolidWorks 2026 VBA programmer.
Generate a complete, runnable VBA macro for SolidWorks 2026.

Rules:
- Convert ALL dimensions from mm to meters (divide by 1000)
- Convert ALL angles from degrees to radians (multiply by π/180)
- Use exact method signatures from the provided API documentation
- Always include: Option Explicit, proper variable declarations, null checks on selections
- Use descriptive feature names
- Structure: Option Explicit → Sub Main() → Declarations → Get swApp/swModel → Null check → Implementation → swModel.ViewZoomtofit2 → End Sub`;
function buildUserMessage(input) {
    const stepsList = input.steps
        .map((s) => `${s.step_number}. [${s.operation.toUpperCase()}] ${s.description}` +
        (Object.keys(s.parameters).length > 0
            ? `\n   Parameters: ${JSON.stringify(s.parameters)}`
            : ""))
        .join("\n");
    let message = "";
    if (input.retry_count > 0) {
        message +=
            `Previous attempt had these errors, please fix them:\n` +
                input.prev_errors.join("\n") +
                `\n\nPrevious code:\n${input.prev_code}\n\n`;
    }
    message += `Generate VBA for these CAD steps:\n\n${stepsList}`;
    if (input.api_docs.length > 0) {
        message += `\n\n## Relevant API Documentation\n${input.api_docs.join("\n\n")}`;
    }
    return message;
}
function stripFences(content) {
    return content
        .replace(/^```vba\n?/i, "")
        .replace(/^```\n?/, "")
        .replace(/```$/i, "")
        .trim();
}
const tracedCode = (0, traceable_1.traceable)(async (input) => {
    const llm = new anthropic_1.ChatAnthropic({
        model: "claude-sonnet-4-5",
        apiKey: config_1.config.ANTHROPIC_API_KEY,
    });
    const userMessage = buildUserMessage(input);
    const response = await llm.invoke([
        new messages_1.SystemMessage(SYSTEM_PROMPT),
        new messages_1.HumanMessage(userMessage),
    ]);
    const rawContent = typeof response.content === "string"
        ? response.content
        : JSON.stringify(response.content);
    return stripFences(rawContent);
}, { name: "coder", run_type: "llm" });
async function code(state) {
    const vba_code = await tracedCode({
        steps: state.steps,
        api_docs: state.api_docs,
        retry_count: state.retry_count,
        prev_errors: state.validation.errors,
        prev_code: state.vba_code,
    });
    return { vba_code };
}
//# sourceMappingURL=coder.js.map