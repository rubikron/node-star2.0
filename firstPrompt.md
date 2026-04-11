# Claude Code Prompt: SolidWorks VBA Macro Generation API (LangGraph)

---

## Claude Code Execution Instructions

You are operating as a **Sonnet orchestrator**. Use the following agent pattern throughout this implementation:

- **You (Sonnet orchestrator):** Plan the work, break it into sub-tasks, delegate each to a Sonnet sub-agent via the Task tool, and integrate the results.
- **Sonnet sub-agents:** Implement each discrete sub-task (a file, a node, a config block). One agent per task.
- **Opus advisor:** If a sub-agent returns stuck, uncertain, or with an error it cannot resolve after two attempts — pause and spawn an Opus agent to diagnose and advise. Then resume the Sonnet sub-agent with that guidance.

**Escalate to Opus when:**
- A LangGraph.js API usage is non-obvious or undocumented
- A TypeScript type error in the graph state or node signatures persists after two fix attempts
- The Pinecone or Neon integration has an edge case the sub-agent cannot resolve
- Any node's logic is ambiguous given the spec below

**Sub-tasks to delegate (in order):**
1. Scaffold project structure, install all dependencies, create `.env.local` template
2. Implement `lib/db/schema.sql` and `lib/db/client.ts` (Neon singleton)
3. Implement `lib/pinecone/client.ts` (Pinecone singleton + query helper)
4. Implement `lib/vba/verify.ts` (pure TS verification function) and `lib/vba/prompts.ts`
5. Implement `lib/graph/state.ts` (VBAState annotation)
6. Implement all 5 nodes in `lib/graph/nodes.ts`
7. Implement `lib/graph/edges.ts` (conditional edge functions)
8. Implement `lib/graph/index.ts` (graph assembly and compile)
9. Implement `app/api/vba/generate/route.ts` (API handler)
10. Create `railway.json` deployment config

Do not proceed to the next sub-task until the current one is complete and verified.

---

## Task

Build a Next.js 14 API route (`/api/vba/generate`) that runs a stateful LangGraph workflow to generate a verified SolidWorks VBA macro. The endpoint accepts a POST request and returns **only raw VBA code** as plain text.

Deploy to **Railway** (Hobby plan). This is a persistent Node.js server — not serverless.

---

## Tech Stack

- **Framework:** Next.js 14 (App Router, TypeScript)
- **Agent Orchestration:** LangGraph.js (`@langchain/langgraph`)
- **LLM:** Anthropic Claude 3.5 Sonnet (`claude-sonnet-4-6`) via `@langchain/anthropic`
- **Vector DB:** Pinecone via `@langchain/pinecone` + `@pinecone-database/pinecone`
- **Embeddings:** OpenAI `text-embedding-3-small` via `@langchain/openai`
- **Web Search:** Tavily via `@langchain/community/tools/tavily_search`
- **SQL DB:** Neon Postgres via `@neondatabase/serverless`
- **Hosting:** Railway (Hobby plan, persistent Node.js server)

---

## Environment Variables

Create `.env.local` (and mirror all in Railway dashboard under Variables):

```
ANTHROPIC_API_KEY=
OPENAI_API_KEY=             # embeddings only
PINECONE_API_KEY=
PINECONE_INDEX=             # your index name
TAVILY_API_KEY=
DATABASE_URL=               # Neon connection string

# Monitoring — choose ONE

# Option A: LangSmith (default, zero extra code)
LANGCHAIN_TRACING_V2=true
LANGCHAIN_API_KEY=          # from smith.langchain.com
LANGCHAIN_PROJECT=solidworks-vba

# Option B: LangFuse (uncomment if switching)
# LANGFUSE_SECRET_KEY=
# LANGFUSE_PUBLIC_KEY=
# LANGFUSE_BASEURL=https://cloud.langfuse.com
```

---

## SQL Schema

Create `lib/db/schema.sql`:

```sql
CREATE TABLE IF NOT EXISTS parts (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  part_id     VARCHAR(100) UNIQUE NOT NULL,
  part_name   VARCHAR(255) NOT NULL,
  part_type   VARCHAR(100) NOT NULL,   -- e.g. "sheet_metal", "extrusion", "fastener"
  description TEXT,
  is_active   BOOLEAN DEFAULT TRUE,
  created_at  TIMESTAMPTZ DEFAULT NOW()
);
```

Create `lib/db/client.ts` — a singleton Neon client using `@neondatabase/serverless`.

---

## Pinecone Index

- **Dimensions:** 1536 (OpenAI `text-embedding-3-small`)
- **Metric:** cosine
- **Namespace:** `solidworks-vba`

Each stored vector's metadata shape:
```json
{
  "part_type": "sheet_metal",
  "action": "add fillet to all edges",
  "vba_code": "Sub AddFillet()\n...\nEnd Sub",
  "description": "Adds a 2mm fillet to all selected edges"
}
```

---

## POST Request Shape

```typescript
// POST /api/vba/generate
{
  "part_id": "SW-BRACKET-001",
  "action": "add a 3mm fillet to all edges",
  "debug": false    // optional — returns full step trace as JSON instead of raw VBA
}
```

---

## LangGraph Workflow

### Graph Structure

```
[check_part]
     │
     ├─ error ──────────────────────────────► END (404)
     │
     ▼
[query_pinecone]
     │
     ├─ hit (score ≥ 0.82) ───────────────► [generate_vba]
     │                                             │
     └─ miss ──────────► [web_search] ────────────┘
                                                   │
                                                   ▼
                                            [verify_vba]
                                                   │
                                   ┌───────────────┴───────────────┐
                                   │                               │
                                 valid                       invalid AND
                                   │                        retry_count < 1
                                   ▼                               │
                                  END                  retry_count++ → [generate_vba]
                               (200)
                                          (retry_count >= 1 → END 422)
```

---

### State Definition

Define in `lib/graph/state.ts`:

```typescript
import { Annotation } from "@langchain/langgraph";

export const VBAState = Annotation.Root({
  // Inputs
  part_id:          Annotation<string>(),
  action:           Annotation<string>(),
  debug:            Annotation<boolean>(),

  // Populated by check_part
  part_type:        Annotation<string | null>(),
  part_description: Annotation<string | null>(),

  // Populated by query_pinecone
  pinecone_vba:     Annotation<string | null>(),
  pinecone_score:   Annotation<number | null>(),

  // Populated by web_search
  search_results:   Annotation<string | null>(),

  // Populated by generate_vba + verify_vba
  raw_vba:          Annotation<string | null>(),
  retry_count:      Annotation<number>(),
  verify_error:     Annotation<string | null>(),
  final_vba:        Annotation<string | null>(),

  // Error handling
  error_status:     Annotation<number | null>(),
  error_message:    Annotation<string | null>(),

  // Debug trace — each node appends its output
  debug_trace:      Annotation<Record<string, unknown>>({
    reducer: (a, b) => ({ ...a, ...b }),
    default: () => ({}),
  }),
});
```

---

### Nodes

Implement all nodes in `lib/graph/nodes.ts`. Each is a typed async function that receives full state and returns a **partial state update**.

---

#### Node 1: `check_part`

*No LLM. Pure SQL.*

```sql
SELECT part_type, description FROM parts WHERE part_id = $1 AND is_active = TRUE
```
- Not found → `{ error_status: 404, error_message: "Part not found: <part_id>" }`
- Found → `{ part_type, part_description }`
- Log: `[check_part] Found: <part_type>`
- Debug trace: `{ check_part: { found, part_type, part_description } }`

---

#### Node 2: `query_pinecone`

*No LLM. Embedding + vector retrieval.*

1. Embed `state.action` using OpenAI `text-embedding-3-small`
2. Query Pinecone (namespace: `solidworks-vba`), top 3, metadata filter: `{ part_type: state.part_type }`
3. Score ≥ 0.82 → `{ pinecone_vba: metadata.vba_code, pinecone_score: score }`
4. Score < 0.82 → `{ pinecone_vba: null, pinecone_score: score }`
- Log: `[query_pinecone] Score: <score> | Hit: <true/false>`
- Debug trace: `{ query_pinecone: { hit, score, vba_preview } }`

---

#### Node 3: `web_search`

*No LLM. Tavily retrieval.*

Use `TavilySearchResults` with `maxResults: 3`. Search query:
```
SolidWorks VBA macro {action} {part_type} site:help.solidworks.com OR site:forum.solidworks.com OR site:stackoverflow.com
```
Concatenate snippets → `{ search_results: string }`.
- Log: `[web_search] Found <n> results`
- Debug trace: `{ web_search: { query, results_count, snippet_preview } }`

---

#### Node 4: `generate_vba`

*Sonnet. Called after Pinecone hit, after web_search, or on retry.*

Model: `claude-sonnet-4-6`

**System prompt:**
```
You are a SolidWorks VBA macro expert. Generate syntactically correct, minimal
SolidWorks VBA macros that run directly in the SolidWorks macro editor.

Rules:
- Output ONLY raw VBA code. No markdown fences, no explanation.
- Every macro must start with Sub and end with End Sub.
- Use the SolidWorks API (swApp, swDoc, swModel) correctly.
- Keep macros concise and focused on the single requested action.
```

**User prompt — build dynamically:**
```
Generate a SolidWorks VBA macro for the following:

Part type: {part_type}
Part description: {part_description}
Action: {action}

{if pinecone_vba}
Similar existing macro for reference:
{pinecone_vba}
{end if}

{if search_results}
Web search reference material:
{search_results}
{end if}

{if retry_count > 0}
Your previous attempt failed verification with error: {verify_error}
Fix the issue and return only corrected VBA code.
{end if}

Return only the raw VBA code.
```

Updates state: `{ raw_vba: string }`
- Log: `[generate_vba] Attempt <retry_count + 1>`
- Debug trace: `{ generate_vba: { attempt, raw_vba_preview } }`

---

#### Node 5: `verify_vba`

*No LLM. Pure TypeScript structural check.*

Create `lib/vba/verify.ts`:

```typescript
export function verifyVBAFormat(code: string): { valid: boolean; error?: string } {
  const trimmed = code.trim();

  if (!/^Sub\s+\w+\s*\(/im.test(trimmed))
    return { valid: false, error: "Missing Sub declaration" };

  if (!/End\s+Sub\s*$/im.test(trimmed))
    return { valid: false, error: "Missing End Sub" };

  if (!/swApp|swDoc|swModel/i.test(trimmed))
    return { valid: false, error: "No SolidWorks API reference found" };

  if (/```/.test(trimmed))
    return { valid: false, error: "Response contains markdown artifacts" };

  return { valid: true };
}
```

- Valid → `{ final_vba: state.raw_vba }`
- Invalid → `{ verify_error: error, retry_count: state.retry_count + 1 }`
- Log: `[verify_vba] Valid: <true/false>`
- Debug trace: `{ verify_vba: { valid, error, retry_count } }`

---

### Conditional Edges

Define in `lib/graph/edges.ts`:

```typescript
export function routeAfterPartCheck(state) {
  return state.error_status ? "__end__" : "query_pinecone";
}

export function routeAfterPinecone(state) {
  return (state.pinecone_score ?? 0) >= 0.82 ? "generate_vba" : "web_search";
}

export function routeAfterVerify(state) {
  if (state.final_vba) return "__end__";            // valid — done
  if (state.retry_count < 1) return "generate_vba"; // retry once
  return "__end__";                                  // give up → 422
}
```

---

### Graph Assembly

Define in `lib/graph/index.ts`:

```typescript
import { StateGraph } from "@langchain/langgraph";
import { VBAState } from "./state";
import { checkPartNode, queryPineconeNode, webSearchNode, generateVBANode, verifyVBANode } from "./nodes";
import { routeAfterPartCheck, routeAfterPinecone, routeAfterVerify } from "./edges";

export const graph = new StateGraph(VBAState)
  .addNode("check_part",     checkPartNode)
  .addNode("query_pinecone", queryPineconeNode)
  .addNode("web_search",     webSearchNode)
  .addNode("generate_vba",   generateVBANode)
  .addNode("verify_vba",     verifyVBANode)
  .addEdge("__start__",      "check_part")
  .addConditionalEdges("check_part",     routeAfterPartCheck)
  .addConditionalEdges("query_pinecone", routeAfterPinecone)
  .addEdge("web_search",                 "generate_vba")
  .addEdge("generate_vba",               "verify_vba")
  .addConditionalEdges("verify_vba",     routeAfterVerify)
  .compile();
```

---

## Monitoring Setup

### Option A — LangSmith (default)
No code changes. LangGraph auto-detects `LANGCHAIN_TRACING_V2` and sends full node-by-node traces to `smith.langchain.com`.

### Option B — LangFuse
Install `langfuse-langchain`, pass a callback into `graph.invoke`:

```typescript
import { CallbackHandler } from "langfuse-langchain";

const langfuseHandler = new CallbackHandler({
  secretKey: process.env.LANGFUSE_SECRET_KEY!,
  publicKey: process.env.LANGFUSE_PUBLIC_KEY!,
  baseUrl: process.env.LANGFUSE_BASEURL!,
});

const result = await graph.invoke(initialState, { callbacks: [langfuseHandler] });
```

---

## API Route Handler

Create `app/api/vba/generate/route.ts`:

```typescript
// Railway is a persistent server — no maxDuration or runtime exports needed

export async function POST(req: Request) {
  const body = await req.json();
  const { part_id, action, debug = false } = body;

  if (!part_id || !action) {
    return Response.json({ error: "part_id and action are required" }, { status: 400 });
  }

  try {
    const result = await graph.invoke({
      part_id, action, debug,
      retry_count: 0,
      part_type: null, part_description: null,
      pinecone_vba: null, pinecone_score: null,
      search_results: null, raw_vba: null,
      verify_error: null, final_vba: null,
      error_status: null, error_message: null,
    });

    if (result.error_status) {
      return Response.json({ error: result.error_message }, { status: result.error_status });
    }

    if (!result.final_vba) {
      return Response.json(
        { error: "VBA generation failed verification", detail: result.verify_error },
        { status: 422 }
      );
    }

    if (debug) {
      return Response.json({ ...result.debug_trace, final_vba: result.final_vba });
    }

    return new Response(result.final_vba, {
      status: 200,
      headers: { "Content-Type": "text/plain" },
    });

  } catch (err) {
    console.error("[route] Unhandled error:", err);
    return Response.json({ error: "Internal server error" }, { status: 500 });
  }
}
```

---

## File Structure

```
/app
  /api/vba/generate
    route.ts
/lib
  /db
    client.ts
    schema.sql
  /pinecone
    client.ts
  /vba
    verify.ts
    prompts.ts
  /graph
    state.ts
    nodes.ts
    edges.ts
    index.ts
/types
  vba.ts
railway.json
```

---

## Error Handling

| Scenario | HTTP | Response |
|---|---|---|
| Missing `part_id` or `action` | 400 | `{ "error": "part_id and action are required" }` |
| Part not in SQL | 404 | `{ "error": "Part not found: <part_id>" }` |
| VBA fails after retry | 422 | `{ "error": "VBA generation failed verification", "detail": "..." }` |
| Unhandled exception | 500 | `{ "error": "Internal server error" }` |

Every node logs `[NodeName] <message>` — visible in Railway's log stream.

---

## Railway Deployment

Create `railway.json` in the project root:

```json
{
  "$schema": "https://railway.app/railway.schema.json",
  "build": {
    "builder": "NIXPACKS"
  },
  "deploy": {
    "startCommand": "npm run build && npm run start",
    "restartPolicyType": "ON_FAILURE",
    "restartPolicyMaxRetries": 3
  }
}
```

Add all environment variables in the Railway dashboard under **Variables**. Connect the GitHub repo — Railway auto-deploys on every push to `main`.

---

## Testing with curl

```bash
# Local dev
curl -X POST http://localhost:3000/api/vba/generate \
  -H "Content-Type: application/json" \
  -d '{"part_id": "SW-BRACKET-001", "action": "add a 3mm fillet to all edges"}'

# Debug mode — full step trace
curl -X POST http://localhost:3000/api/vba/generate \
  -H "Content-Type: application/json" \
  -d '{"part_id": "SW-BRACKET-001", "action": "add a 3mm fillet to all edges", "debug": true}'

# Railway production
curl -X POST https://<your-app>.up.railway.app/api/vba/generate \
  -H "Content-Type: application/json" \
  -d '{"part_id": "SW-BRACKET-001", "action": "add a 3mm fillet to all edges"}'
```

---

## Design Principles for Future Expansion

- **New tool node:** add a function in `nodes.ts`, register in `index.ts`, add a conditional edge. Nothing else changes.
- **Human-in-the-loop:** add `interrupt_before: ["generate_vba"]` to the compile step.
- **Parallelism:** Pinecone and web search can run in parallel via the LangGraph `Send` API.
- **Persistence:** add a Postgres-backed LangGraph checkpointer to `graph.compile({ checkpointer })` for multi-turn workflows.
