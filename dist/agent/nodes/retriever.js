"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.retrieve = retrieve;
const pinecone_1 = require("@pinecone-database/pinecone");
const traceable_1 = require("langsmith/traceable");
const config_1 = require("../../config");
const pc = new pinecone_1.Pinecone({ apiKey: config_1.config.PINECONE_API_KEY });
const index = pc.index(config_1.config.PINECONE_INDEX);
const MIN_SCORE = 0.7;
function extractTextFromHit(hit) {
    const fields = hit.fields;
    if (!fields)
        return "";
    return (fields["text"] ??
        fields["content"] ??
        fields["chunk"] ??
        "");
}
const tracedRetrieve = (0, traceable_1.traceable)(async (steps) => {
    const chunks = [];
    const seen = new Set();
    for (const step of steps) {
        const queryText = `${step.operation}: ${step.description}`;
        let hits = [];
        try {
            const searchIndex = index;
            const results = await searchIndex.searchRecords({
                query: { inputs: { text: queryText }, topK: 3 },
                fields: ["text", "content", "chunk"],
            });
            hits =
                results.result?.hits ??
                    results.hits ??
                    [];
        }
        catch {
            // searchRecords not available — skip this step
            continue;
        }
        for (const hit of hits) {
            const score = hit.score ?? hit._score ?? 1;
            if (score < MIN_SCORE)
                continue;
            const text = extractTextFromHit(hit);
            if (text && !seen.has(text)) {
                seen.add(text);
                chunks.push(text);
            }
        }
    }
    return chunks;
}, { name: "retriever", run_type: "retriever" });
async function retrieve(state) {
    try {
        const api_docs = await tracedRetrieve(state.steps);
        return { api_docs };
    }
    catch {
        return { api_docs: [] };
    }
}
//# sourceMappingURL=retriever.js.map