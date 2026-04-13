import "../config";
import Fastify from "fastify";
import OpenAI from "openai";
import { wrapOpenAI } from "langsmith/wrappers";
import { config } from "../config";
import type { ChatCompletionRequest } from "./types";

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

      // Generate VBA code from user request
      const response = await openai.chat.completions.create({
        model: "gpt-5.4-mini",
        max_completion_tokens: 4096,
        messages: [
          {
            role: "system",
            content: "Output only raw VBA code. No markdown, no text before or after the code. VBA comments inside the code are fine.",
          },
          { role: "user", content: userContent },
        ],
      });

      const vba = (response.choices[0]?.message?.content ?? "")
        .replace(/^```vba\n?/im, "")
        .replace(/^```\n?/m, "")
        .replace(/```$/m, "")
        .trim();

      console.log(`[generate] ${vba.split("\n").length} lines`);

      // Generate metadata: name, description, and chat response
      const metaResponse = await openai.chat.completions.create({
        model: "gpt-5.4-mini",
        max_completion_tokens: 512,
        response_format: { type: "json_object" },
        messages: [
          {
            role: "system",
            content: `Given a SolidWorks VBA macro and the user request that produced it, return JSON with exactly these fields:
{
  "name": "Human-readable macro name (e.g. 'Create Cube with Fillets')",
  "description": "One sentence: what the macro does and any preconditions (e.g. requires an open part document).",
  "response": "2-3 sentence chat reply to the user explaining what was generated and how to use it."
}`,
          },
          {
            role: "user",
            content: `User request: ${userContent}\n\nGenerated VBA:\n${vba}`,
          },
        ],
      });

      let meta = { name: "SolidWorks Macro", description: "", response: "" };
      try {
        meta = JSON.parse(metaResponse.choices[0]?.message?.content ?? "{}");
      } catch { /* keep defaults */ }

      return {
        name: meta.name,
        description: meta.description,
        vba,
        response: meta.response,
      };
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
