import "../config";
import Fastify from "fastify";
import OpenAI from "openai";
import { wrapOpenAI } from "langsmith/wrappers";
import { v4 as uuidv4 } from "uuid";
import { config } from "../config";
import type { ChatCompletionRequest, ChatCompletionResponse } from "./types";

const server = Fastify({ logger: true });
const openai = wrapOpenAI(new OpenAI({ apiKey: config.OPENAI_API_KEY }));


server.addHook("onSend", async (_req, reply) => {
  reply.header("Access-Control-Allow-Origin", "*");
  reply.header("Access-Control-Allow-Methods", "GET,POST,OPTIONS");
  reply.header("Access-Control-Allow-Headers", "Content-Type,Authorization");
});
server.addHook("preHandler", async (req, reply) => {
  if (req.method === "OPTIONS") reply.code(204).send();
});

server.get("/", async () => ({ service: "solidworks-vba-gen", version: "1.0.0" }));
server.get("/health", async () => ({ status: "ok" }));

server.post<{ Body: ChatCompletionRequest }>(
  "/v1/chat/completions",
  async (request, reply) => {
    try {
      const messages = request.body.messages ?? [];
      const lastUser = [...messages].reverse().find((m) => m.role === "user");
      const userContent = lastUser?.content ?? "";

      const response = await openai.chat.completions.create({
        model: "gpt-5.4-mini",
        max_completion_tokens: 4096,
        messages: [
          { role: "user", content: userContent },
        ],
      });

      const vba = (response.choices[0]?.message?.content ?? "")
        .replace(/^```vba\n?/im, "")
        .replace(/^```\n?/m, "")
        .replace(/```$/m, "")
        .trim();

      console.log(`[generate] ${vba.split("\n").length} lines`);

      const result: ChatCompletionResponse = {
        id: "chatcmpl-" + uuidv4().replace(/-/g, "").slice(0, 8),
        object: "chat.completion",
        created: Math.floor(Date.now() / 1000),
        model: "solidworks-vba-gen",
        choices: [
          {
            index: 0,
            message: { role: "assistant", content: `\`\`\`vba\n${vba}\n\`\`\`` },
            finish_reason: "stop",
          },
        ],
        usage: {
          prompt_tokens: response.usage?.prompt_tokens ?? 0,
          completion_tokens: response.usage?.completion_tokens ?? 0,
          total_tokens: response.usage?.total_tokens ?? 0,
        },
      };

      return result;
    } catch (err: unknown) {
      reply.code(500).send({ error: { message: String(err), type: "server_error" } });
    }
  }
);

const start = async () => {
  try {
    await server.listen({ port: config.PORT, host: "0.0.0.0" });
  } catch (err) {
    server.log.error(err);
    process.exit(1);
  }
};

start();
export { server };
