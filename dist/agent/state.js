"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.GraphStateAnnotation = void 0;
const langgraph_1 = require("@langchain/langgraph");
exports.GraphStateAnnotation = langgraph_1.Annotation.Root({
    messages: (0, langgraph_1.Annotation)({
        reducer: (a, b) => [...a, ...b],
        default: () => [],
    }),
    steps: (0, langgraph_1.Annotation)({
        reducer: (_, b) => b,
        default: () => [],
    }),
    api_docs: (0, langgraph_1.Annotation)({
        reducer: (_, b) => b,
        default: () => [],
    }),
    vba_code: (0, langgraph_1.Annotation)({
        reducer: (_, b) => b,
        default: () => "",
    }),
    validation: (0, langgraph_1.Annotation)({
        reducer: (_, b) => b,
        default: () => ({ is_valid: false, errors: [], corrected_code: "" }),
    }),
    retry_count: (0, langgraph_1.Annotation)({
        reducer: (_, b) => b,
        default: () => 0,
    }),
});
//# sourceMappingURL=state.js.map