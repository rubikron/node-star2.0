// Set required env vars before any imports so config.ts does not throw
process.env.ANTHROPIC_API_KEY = "test-key";
process.env.PINECONE_API_KEY = "test-key";
process.env.LANGSMITH_API_KEY = "test-key";
process.env.PINECONE_INDEX = "test-index";
process.env.LANGSMITH_PROJECT = "test-project";
process.env.LANGSMITH_TRACING_V2 = "false";
process.env.PORT = "0";

// Mock the LangGraph app before importing the server so that graph.ts is never
// evaluated (which would try to connect to Pinecone / Anthropic).
jest.mock("../src/agent/graph", () => ({
  app: {
    invoke: jest.fn().mockResolvedValue({
      messages: [],
      steps: [
        {
          step_number: 1,
          operation: "extrude",
          description: "test",
          parameters: {},
        },
      ],
      api_docs: [],
      vba_code:
        "Option Explicit\nSub Main()\n    Dim swApp As SldWorks.SldWorks\nEnd Sub",
      validation: { is_valid: true, errors: [], corrected_code: "" },
      retry_count: 0,
    }),
  },
}));

import { server } from "../src/server/main";

afterAll(async () => {
  await server.close();
});

describe("GET /health", () => {
  it("returns 200 with { status: 'ok' }", async () => {
    const response = await server.inject({ method: "GET", url: "/health" });
    expect(response.statusCode).toBe(200);
    const body = JSON.parse(response.body) as Record<string, unknown>;
    expect(body.status).toBe("ok");
  });
});

describe("GET /", () => {
  it("returns 200 with a service field", async () => {
    const response = await server.inject({ method: "GET", url: "/" });
    expect(response.statusCode).toBe(200);
    const body = JSON.parse(response.body) as Record<string, unknown>;
    expect(body).toHaveProperty("service");
  });
});

describe("POST /v1/chat/completions", () => {
  const validPayload = {
    model: "solidworks-vba-gen",
    messages: [{ role: "user", content: "Draw a 50mm circle" }],
  };

  it("returns 200 with object: 'chat.completion'", async () => {
    const response = await server.inject({
      method: "POST",
      url: "/v1/chat/completions",
      headers: { "content-type": "application/json" },
      payload: JSON.stringify(validPayload),
    });
    expect(response.statusCode).toBe(200);
    const body = JSON.parse(response.body) as Record<string, unknown>;
    expect(body.object).toBe("chat.completion");
  });

  it("returns choices[0].message.role === 'assistant'", async () => {
    const response = await server.inject({
      method: "POST",
      url: "/v1/chat/completions",
      headers: { "content-type": "application/json" },
      payload: JSON.stringify(validPayload),
    });
    expect(response.statusCode).toBe(200);
    const body = JSON.parse(response.body) as {
      choices: Array<{ message: { role: string; content: string } }>;
    };
    expect(body.choices[0].message.role).toBe("assistant");
  });

  it("response content contains 'vba' (case-insensitive)", async () => {
    const response = await server.inject({
      method: "POST",
      url: "/v1/chat/completions",
      headers: { "content-type": "application/json" },
      payload: JSON.stringify(validPayload),
    });
    expect(response.statusCode).toBe(200);
    const body = JSON.parse(response.body) as {
      choices: Array<{ message: { content: string } }>;
    };
    expect(body.choices[0].message.content.toLowerCase()).toContain("vba");
  });

  it("returns 200 or a 4xx status (not a 500 crash) when messages array is empty", async () => {
    const response = await server.inject({
      method: "POST",
      url: "/v1/chat/completions",
      headers: { "content-type": "application/json" },
      payload: JSON.stringify({ model: "solidworks-vba-gen", messages: [] }),
    });
    expect(response.statusCode).not.toBe(500);
  });
});
