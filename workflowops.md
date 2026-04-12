# Workflow Operations

This document describes the end-to-end message flow for the SolidWorks VBA Macro Generation API. **Keep this file up to date whenever the workflow changes.** See `CLAUDE.md` for the update rule.

---

## Entry Points

### 1. OpenAI-Compatible Chat Endpoint (primary)

**`POST /api/v1/chat/completions`** — `app/api/v1/chat/completions/route.ts`

Accepts OpenAI Chat Completions format. A Haiku planner layer parses the user's natural-language message, extracts part IDs and actions, plans the execution sequence, then invokes the LangGraph workflow once per planned execution.

#### Request Body
```json
{
  "model": "node-star-vba-1",
  "messages": [
    { "role": "user", "content": "Add a chamfer to the edges of SW-001" }
  ]
}
```

- `messages` *(required)* — OpenAI-format message array; must contain at least one message
- `model` *(optional)* — echoed back in the response; defaults to `"node-star-vba-1"`

#### Response Body (200)
```json
{
  "id": "chatcmpl-<uuid>",
  "object": "chat.completion",
  "created": 1234567890,
  "model": "node-star-vba-1",
  "choices": [{ "index": 0, "message": { "role": "assistant", "content": "<raw VBA>" }, "finish_reason": "stop" }],
  "usage": { "prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0 }
}
```

For multi-execution responses (multiple parts), `content` contains each VBA block separated by a comment header: `' === [Step N] <description> ===`.

#### Haiku Planner (`lib/planner/index.ts`)
- Model: `claude-haiku-4-5-20251001`
- Uses tool calling (`create_execution_plan`) to return a structured `ExecutionPlan`
- Extracts `part_id` values only from user-supplied text — never invents IDs
- Returns `executions[]` sorted by `sequence_number`; executions run sequentially

#### Validation
- Missing or empty `messages` → **400**
- No part IDs found in message → **400**
- Invalid JSON body → **400**

---

### 2. Direct VBA Generation Endpoint (internal / legacy)

**`POST /api/vba/generate`** — `app/api/vba/generate/route.ts`

### Request Body
```json
{
  "part_id": "SW-001",
  "action": "add a 3mm fillet to all edges",
  "debug": false
}
```

- `part_id` *(required)* — identifier looked up in the Neon parts table
- `action` *(required)* — natural-language description of the VBA operation
- `debug` *(optional, default false)* — if true, returns full node trace as JSON instead of raw VBA

### Validation
- Missing `part_id` or `action` → **400**
- Invalid JSON body → **400**

---

## LangGraph State

Defined in `lib/graph/state.ts` as `VBAState`. All nodes read from and write partial updates to this shared state object.

| Field | Set by | Purpose |
|---|---|---|
| `part_id` | route input | SQL lookup key |
| `action` | route input | Natural-language operation |
| `debug` | route input | Enable debug trace response |
| `part_type` | `check_part` | Filters Pinecone by part category |
| `part_description` | `check_part` | Optional context for LLM |
| `pinecone_vba` | `query_pinecone` | Cache-hit VBA snippet (if score ≥ 0.82) |
| `pinecone_score` | `query_pinecone` | Similarity score from vector search |
| `search_results` | `web_search` | Tavily search content injected into LLM prompt |
| `raw_vba` | `generate_vba` | Raw LLM output before verification |
| `retry_count` | `verify_vba` | Incremented on each failed verification |
| `verify_error` | `verify_vba` | Error message fed back to LLM on retry |
| `final_vba` | `verify_vba` | Verified, clean VBA — only set on success |
| `error_status` | `check_part` | HTTP status code on hard error |
| `error_message` | `check_part` | Error detail on hard error |
| `debug_trace` | all nodes | Merged map of per-node debug info |

---

## Graph Flow

```
START
  │
  ▼
check_part ──(error_status set)──────────────────► END (error)
  │
  │ (no error)
  ▼
query_pinecone
  │
  ├──(score ≥ 0.82, cache hit)──────────────────► generate_vba
  │                                                    │
  └──(score < 0.82, cache miss)──► web_search ───────►┘
                                                       │
                                                       ▼
                                                  verify_vba
                                                       │
                                         ┌─────────────┴──────────────┐
                                         │                            │
                                  (valid, final_vba set)    (invalid, retry_count < 2)
                                         │                            │
                                         ▼                            ▼
                                        END (200)              generate_vba (retry)
                                                                      │
                                                                      ▼
                                                                 verify_vba
                                                                      │
                                                         ┌────────────┴────────────┐
                                                         │                         │
                                                  (valid)                  (invalid, retry_count ≥ 2)
                                                         │                         │
                                                        END (200)               END (422)
```

---

## Node Details

### Node 1 — `check_part`
**File:** `lib/graph/nodes.ts` → `checkPartNode`
**Type:** SQL lookup, no LLM

- Queries Neon Postgres `parts` table for `part_id`
- On success: sets `part_type` and `part_description`
- On not found or DB error: defaults `part_type` to `"general"`, continues flow (DB is optional)
- Only sets `error_status` for hard validation errors (currently unused — DB failures are soft)

**Logs:** `[check_part] Found: <part_type>` or `[check_part] DB unavailable, skipping: <error>`

---

### Node 2 — `query_pinecone`
**File:** `lib/graph/nodes.ts` → `queryPineconeNode`
**Type:** Vector search, no LLM

- Embeds `action` using OpenAI `text-embedding-3-small` (1024 dimensions)
- Queries Pinecone namespace `solidworks-vba`, filtered by `part_type`
- `topK: 3`, uses top match score
- Score ≥ 0.82 → cache hit, sets `pinecone_vba`, skips `web_search`
- Score < 0.82 → cache miss, routes to `web_search`

**Logs:** `[query_pinecone] Score: <score> | Hit: <true/false>`

---

### Node 3 — `web_search`
**File:** `lib/graph/nodes.ts` → `webSearchNode`
**Type:** Web retrieval, no LLM

- Only runs on Pinecone cache miss
- Searches via Tavily (`TavilySearchAPIRetriever`, `k: 3`)
- Query targets: `help.solidworks.com`, `forum.solidworks.com`, `stackoverflow.com`
- Sets `search_results` (concatenated page content)

**Logs:** `[web_search] Found <n> results`

---

### Node 4 — `generate_vba`
**File:** `lib/graph/nodes.ts` → `generateVBANode`
**LLM:** Claude `claude-sonnet-4-6` via `@langchain/anthropic`

- Builds prompt via `buildUserPrompt()` (`lib/vba/prompts.ts`) injecting:
  - `part_type`, `part_description`, `action`
  - `pinecone_vba` (if cache hit)
  - `search_results` (if web search ran)
  - `verify_error` + retry context (on retry)
- System prompt from `VBA_SYSTEM_PROMPT` (`lib/vba/prompts.ts`) — defines boilerplate, unit rules, API patterns
- Sets `raw_vba`

**Logs:** `[generate_vba] Attempt <n>`

---

### Node 5 — `verify_vba`
**File:** `lib/graph/nodes.ts` → `verifyVBANode`
**Type:** Pure TypeScript structural check, no LLM

- Calls `verifyVBAFormat()` (`lib/vba/verify.ts`)
- Checks: contains `Sub`/`End Sub`, references SolidWorks API (`swApp`/`swDoc`/`swModel`), no markdown fences
- Pass: sets `final_vba`, routes to END
- Fail: increments `retry_count`, sets `verify_error`, routes back to `generate_vba` if `retry_count < 2`
- Max 1 retry (2 total attempts). After 2 failures → END, route returns 422

**Logs:** `[verify_vba] Valid: <true/false>`

---

## Response Formats

| Status | Condition | Body |
|---|---|---|
| 200 | Success, `debug: false` | Raw VBA as `text/plain` |
| 200 | Success, `debug: true` | JSON with `debug_trace` + `final_vba` |
| 400 | Missing fields or bad JSON | `{ "error": "..." }` |
| 422 | VBA failed verification after retry | `{ "error": "...", "detail": "..." }` |
| 500 | Unhandled exception | `{ "error": "Internal server error" }` |

---

## Key Files Reference

| File | Role |
|---|---|
| `app/api/v1/chat/completions/route.ts` | OpenAI-compatible endpoint with Haiku planner layer |
| `lib/planner/index.ts` | Haiku planner — parses intent, extracts parts, plans execution sequence |
| `types/openai.ts` | OpenAI Chat Completions request/response types |
| `app/api/vba/generate/route.ts` | Direct HTTP handler, input validation, response formatting |
| `lib/graph/state.ts` | LangGraph state schema (`VBAState`) |
| `lib/graph/nodes.ts` | All 5 node implementations |
| `lib/graph/edges.ts` | Conditional routing functions |
| `lib/graph/index.ts` | Graph assembly and compile |
| `lib/vba/prompts.ts` | System prompt + user prompt builder |
| `lib/vba/verify.ts` | Pure TS VBA format checker |
| `lib/pinecone/client.ts` | Pinecone singleton + `queryVBASnippets()` |
| `lib/db/client.ts` | Neon Postgres singleton + `getPartByPartId()` |
| `scripts/seed-pinecone.mjs` | One-time script to seed Pinecone with VBA snippets |
