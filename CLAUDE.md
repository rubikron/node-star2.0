# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**node-star2.0** is a SolidWorks VBA Macro Generation API for Node-Star Company. It is a Next.js 14 API service that accepts a part ID and action, runs a LangGraph stateful workflow, and returns raw SolidWorks VBA code as plain text.

The full implementation spec lives in `firstPrompt.md`. The `llmAPI.md` file contains a high-level workflow summary.

## Target Architecture

Deploy target: **Railway** (persistent Node.js server — not serverless/Vercel).

**Endpoint:** `POST /api/vba/generate`
**Response:** Raw VBA code as `text/plain`, or JSON error.

### Tech Stack

- **Framework:** Next.js 14 (App Router, TypeScript)
- **Agent Orchestration:** LangGraph.js (`@langchain/langgraph`)
- **LLM:** Claude `claude-sonnet-4-6` via `@langchain/anthropic`
- **Vector DB:** Pinecone (`@langchain/pinecone` + `@pinecone-database/pinecone`), namespace `solidworks-vba`
- **Embeddings:** OpenAI `text-embedding-3-small` via `@langchain/openai`
- **Web Search:** Tavily via `@langchain/community/tools/tavily_search`
- **SQL DB:** Neon Postgres via `@neondatabase/serverless`
- **Monitoring:** LangSmith (default) or LangFuse

### Planned File Structure

```
/app/api/vba/generate/route.ts   — API handler
/lib/db/client.ts                — Neon singleton
/lib/db/schema.sql               — parts table
/lib/pinecone/client.ts          — Pinecone singleton + query helper
/lib/vba/verify.ts               — pure TS VBA structural checker
/lib/vba/prompts.ts              — LLM prompt templates
/lib/graph/state.ts              — VBAState annotation (LangGraph)
/lib/graph/nodes.ts              — all 5 graph nodes
/lib/graph/edges.ts              — conditional edge functions
/lib/graph/index.ts              — graph assembly and compile
/types/vba.ts                    — shared types
railway.json                     — Railway deployment config
```

## LangGraph Workflow

The graph runs these nodes in order, with conditional branching:

1. **`check_part`** — SQL lookup in `parts` table; returns 404 if not found
2. **`query_pinecone`** — embeds action, queries Pinecone; score ≥ 0.82 = hit → skip to `generate_vba`; miss → `web_search`
3. **`web_search`** — Tavily search targeting SolidWorks-specific sites
4. **`generate_vba`** — Claude Sonnet generates raw VBA using context from Pinecone/web; retries once on verify failure
5. **`verify_vba`** — pure TS check: must have `Sub`/`End Sub`, SolidWorks API refs (`swApp`/`swDoc`/`swModel`), no markdown artifacts

Retry logic: `verify_vba` → `generate_vba` → `verify_vba` → END 422 (max 1 retry).

## Development Commands

```bash
# Install dependencies (once scaffolded)
npm install

# Local dev server
npm run dev

# Build
npm run build

# Start production server
npm run start

# Test the endpoint locally
curl -X POST http://localhost:3000/api/vba/generate \
  -H "Content-Type: application/json" \
  -d '{"part_id": "SW-BRACKET-001", "action": "add a 3mm fillet to all edges"}'

# Debug mode (returns full step trace as JSON)
curl -X POST http://localhost:3000/api/vba/generate \
  -H "Content-Type: application/json" \
  -d '{"part_id": "SW-BRACKET-001", "action": "add a 3mm fillet to all edges", "debug": true}'
```

## Environment Variables

Create `.env.local` (mirror all in Railway dashboard under Variables):

```
ANTHROPIC_API_KEY=
OPENAI_API_KEY=           # embeddings only
PINECONE_API_KEY=
PINECONE_INDEX=           # your index name
TAVILY_API_KEY=
DATABASE_URL=             # Neon connection string

# LangSmith monitoring (default, no extra code needed)
LANGCHAIN_TRACING_V2=true
LANGCHAIN_API_KEY=
LANGCHAIN_PROJECT=solidworks-vba
```

## API Contract

| Status | Scenario |
|--------|----------|
| 200 | Success — returns raw VBA as `text/plain` |
| 400 | Missing `part_id` or `action` |
| 404 | Part not found in SQL |
| 422 | VBA failed verification after retry |
| 500 | Unhandled exception |

## Key Implementation Notes

- **State immutability:** each graph node returns a partial state update — never mutate the state object directly
- **Pinecone metadata shape:** `{ part_type, action, vba_code, description }` — filter queries by `part_type`
- **VBA verification is pure TS** — no LLM call in `verify_vba`; see `verifyVBAFormat()` in `firstPrompt.md`
- **Logging convention:** every node logs `[NodeName] <message>` for Railway log stream visibility
- **Debug trace:** each node appends to `debug_trace` using the reducer defined in `VBAState`
- **Railway deployment:** persistent server — do not add `export const runtime = 'edge'` or `maxDuration` to the route
- **Expanding the graph:** add new tool nodes in `nodes.ts`, register in `index.ts`, wire a conditional edge — no other files change
