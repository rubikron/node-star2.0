"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.app = void 0;
require("../config");
// eslint-disable-next-line @typescript-eslint/no-require-imports
const langgraph_1 = require("@langchain/langgraph");
const state_1 = require("./state");
const decomposer_1 = require("./nodes/decomposer");
const retriever_1 = require("./nodes/retriever");
const coder_1 = require("./nodes/coder");
const validator_1 = require("./nodes/validator");
function routeAfterValidation(state) {
    if (state.validation.is_valid)
        return langgraph_1.END;
    if (state.retry_count < 1)
        return "increment_retry";
    return langgraph_1.END;
}
// StateGraph's TypeScript generics track registered node names through a
// builder-pattern type-state that does not update when nodes are added via
// mutation. Cast to `any` for wiring calls; runtime behavior is correct as
// verified by the addNode calls above.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const builder = new langgraph_1.StateGraph(state_1.GraphStateAnnotation);
builder.addNode("decompose", decomposer_1.decompose);
builder.addNode("retrieve", retriever_1.retrieve);
builder.addNode("code", coder_1.code);
builder.addNode("validate", validator_1.validate);
builder.addNode("increment_retry", async (state) => ({
    retry_count: state.retry_count + 1,
}));
builder.addEdge(langgraph_1.START, "decompose");
builder.addEdge("decompose", "retrieve");
builder.addEdge("retrieve", "code");
builder.addEdge("code", "validate");
builder.addConditionalEdges("validate", routeAfterValidation, {
    increment_retry: "increment_retry",
    [langgraph_1.END]: langgraph_1.END,
});
builder.addEdge("increment_retry", "code");
exports.app = builder.compile();
//# sourceMappingURL=graph.js.map