"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.server = void 0;
require("../config"); // must be first — sets env vars
const fastify_1 = __importDefault(require("fastify"));
const uuid_1 = require("uuid");
const messages_1 = require("@langchain/core/messages");
const graph_1 = require("../agent/graph");
const config_1 = require("../config");
const server = (0, fastify_1.default)({ logger: true });
exports.server = server;
// CORS hooks
server.addHook("onSend", async (_request, reply) => {
    reply.header("Access-Control-Allow-Origin", "*");
    reply.header("Access-Control-Allow-Methods", "GET,POST,OPTIONS");
    reply.header("Access-Control-Allow-Headers", "Content-Type,Authorization");
});
server.addHook("preHandler", async (request, reply) => {
    if (request.method === "OPTIONS") {
        reply.code(204).send();
    }
});
// GET /
server.get("/", async (_request, _reply) => {
    return {
        service: "solidworks-vba-gen",
        version: "1.0.0",
        docs: "/documentation",
    };
});
// GET /health
server.get("/health", async (_request, _reply) => {
    return { status: "ok", service: "solidworks-vba-gen" };
});
// POST /v1/chat/completions
server.post("/v1/chat/completions", async (request, reply) => {
    try {
        const body = request.body;
        const messages = body.messages ?? [];
        const lastUserMessage = [...messages]
            .reverse()
            .find((m) => m.role === "user");
        const userContent = lastUserMessage?.content ?? "";
        const initialState = {
            messages: [new messages_1.HumanMessage(userContent)],
            steps: [],
            api_docs: [],
            vba_code: "",
            validation: { is_valid: false, errors: [], corrected_code: "" },
            retry_count: 0,
        };
        const result = await graph_1.app.invoke(initialState);
        const rawVba = result.validation?.corrected_code?.trim()
            ? result.validation.corrected_code
            : result.vba_code ?? "";
        let finalContent = `\`\`\`vba\n${rawVba}\n\`\`\``;
        const isValid = result.validation?.is_valid ?? false;
        const errors = result.validation?.errors ?? [];
        if (!isValid && errors.length > 0) {
            const errorComments = errors
                .map((e) => `' ERROR: ${e}`)
                .join("\n");
            finalContent = `${errorComments}\n\n${finalContent}`;
        }
        const responseId = "chatcmpl-" + (0, uuid_1.v4)().replace(/-/g, "").slice(0, 8);
        const response = {
            id: responseId,
            object: "chat.completion",
            created: Math.floor(Date.now() / 1000),
            model: "solidworks-vba-gen",
            choices: [
                {
                    index: 0,
                    message: { role: "assistant", content: finalContent },
                    finish_reason: "stop",
                },
            ],
            usage: {
                prompt_tokens: 0,
                completion_tokens: 0,
                total_tokens: 0,
            },
        };
        return response;
    }
    catch (err) {
        reply.code(500).send({
            error: {
                message: String(err),
                type: "server_error",
            },
        });
    }
});
const start = async () => {
    try {
        await server.listen({ port: config_1.config.PORT, host: "0.0.0.0" });
    }
    catch (err) {
        server.log.error(err);
        process.exit(1);
    }
};
start();
//# sourceMappingURL=main.js.map