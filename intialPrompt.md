# Claude Code Prompt for SolidWorks VBA Agent

```markdown
# Task: Build SolidWorks VBA Code Generation Agent

Build a production-ready LangGraph agent that generates SolidWorks 2026 VBA macros from natural language requests. Deploy on Railway with an OpenAI-compatible API endpoint and full LangSmith tracing.

## Architecture Overview

```
User Request 
    ↓
┌─────────────────┐
│   Decomposer    │  Break request into atomic CAD steps
└────────┬────────┘
         ↓
┌─────────────────┐
│    Retriever    │  Fetch relevant API docs from Pinecone
└────────┬────────┘
         ↓
┌─────────────────┐
│     Coder       │  Generate VBA macro using steps + docs
└────────┬────────┘
         ↓
┌─────────────────┐
│    Validator    │  Check syntax + units + API correctness
└────────┬────────┘
         ↓
    ┌────┴────┐
    │ Valid?  │
    └────┬────┘
     Yes │ No (max 1 retry)
         ↓         ↓
      Output    Retry → Coder (with error context)
```

## Tech Stack

- **Framework:** LangGraph
- **LLM:** Claude Sonnet 4 via Anthropic API (`claude-sonnet-4-20250514`)
- **Embeddings:** OpenAI `text-embedding-3-small`
- **Vector DB:** Pinecone (index: `solidworks-api`) — already populated with SolidWorks 2026 API markdown docs chunked by method
- **Tracing:** LangSmith (project: `solidworks-vba-gen`)
- **Server:** FastAPI on Railway
- **API Format:** OpenAI-compatible `/v1/chat/completions`

## Environment Variables

```
ANTHROPIC_API_KEY
OPENAI_API_KEY          # embeddings only
PINECONE_API_KEY
PINECONE_INDEX=solidworks-api
LANGSMITH_API_KEY
LANGSMITH_PROJECT=solidworks-vba-gen
LANGSMITH_TRACING_V2=true
```

## Node Specifications

### 1. Decomposer Node
**Input:** User's natural language request  
**Output:** Ordered list of atomic CAD steps

Responsibilities:
- Parse intent (create part, modify, assembly, etc.)
- Break into minimal operations: sketch, extrude, cut, revolve, fillet, chamfer, shell, pattern, hole, mate
- Preserve correct ordering (base features → detail features, shell before ribs, fillets last)
- Extract dimensions and parameters in millimeters (coder converts to meters)

Output schema:
```json
{
  "steps": [
    {
      "step_number": 1,
      "operation": "sketch | extrude | cut | revolve | fillet | chamfer | shell | pattern | hole | mate",
      "description": "Human-readable description",
      "parameters": {}
    }
  ]
}
```

### 2. Retriever Node
**Input:** List of steps from Decomposer  
**Output:** Relevant API documentation chunks

Responsibilities:
- For each step, embed the operation + description
- Query Pinecone for top-k relevant API docs (k=2-3 per step)
- Deduplicate across steps
- Filter by relevance score (threshold ~0.7)

The Pinecone index contains markdown chunks with:
- Method signatures
- Parameter tables (types, descriptions)
- Working VBA examples
- Common errors
- Unit notes (meters, radians)

### 3. Coder Node
**Input:** Steps + Retrieved API docs  
**Output:** Complete VBA macro

Responsibilities:
- Generate complete, runnable VBA Sub
- Convert all dimensions mm → meters (÷1000)
- Convert all angles degrees → radians (×π/180)
- Use exact method signatures from retrieved docs
- Include proper variable declarations (Option Explicit)
- Add error handling for selections
- Name features descriptively

Code structure to follow:
```vba
' [Description]
' Generated for SolidWorks 2026
Option Explicit

Sub Main()
    ' Declarations
    ' Get application/model
    ' Null check
    ' Implementation using retrieved API methods
    ' Cleanup/zoom to fit
End Sub
```

### 4. Validator Node
**Input:** Generated VBA code + Retrieved docs  
**Output:** Validation result, optionally corrected code

Two-phase validation:

**Phase 1 - Deterministic checks:**
- Sub/End Sub present
- SldWorks declarations present
- Unit sanity (flag values >1 in dimension parameters as likely mm-not-meters errors)
- Option Explicit present

**Phase 2 - LLM semantic check:**
- Compare method signatures against retrieved docs
- Verify parameter order and types
- Check for missing error handling on SelectByID2 calls
- Validate logical flow

Output schema:
```json
{
  "is_valid": true,
  "errors": [],
  "corrected_code": ""
}
```

If invalid, return corrected_code with fixes applied.

## Graph Flow Logic

```
START → decompose → retrieve → code → validate
                                          ↓
                                    ┌─────┴─────┐
                                    │  is_valid │
                                    └─────┬─────┘
                                   Yes    │    No
                                    ↓     │     ↓
                                   END    │   retry_count < 1?
                                          │     ↓
                                          │   Yes → code (with errors in context) → validate
                                          │   No → END (return best effort + errors)
```

- Maximum 1 retry to keep latency reasonable
- On retry, pass validation errors to coder node as additional context
- Always return something — partial code with error notes is better than nothing

## API Endpoint Specification

```
POST /v1/chat/completions

Request:
{
  "model": "solidworks-vba-gen",
  "messages": [
    {"role": "user", "content": "Create a 50mm cube with 5mm fillets on all edges"}
  ],
  "temperature": 0.2
}

Response:
{
  "id": "chatcmpl-xxx",
  "object": "chat.completion",
  "created": 1699000000,
  "model": "solidworks-vba-gen",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "```vba\n' Create 50mm cube with 5mm fillets...\nOption Explicit\n\nSub Main()...\n```"
      },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": 0,
    "completion_tokens": 0,
    "total_tokens": 0
  }
}
```

Also expose:
- `GET /health` — health check for Railway
- `GET /` — basic info/docs redirect

## LangSmith Tracing Requirements

All nodes must be traced with:
- Node name as run name
- Input/output logged
- Latency tracked
- Token usage where applicable

The full graph execution should appear as a single trace with child spans for each node.

## Deployment Notes

- Use `uvicorn` with `--host 0.0.0.0 --port $PORT`
- Railway will set `PORT` env var
- Include `railway.json` or `Procfile`
- Keep cold start reasonable — lazy-load Pinecone connection

## Supported Operations

The system should handle these SolidWorks operations (maps to Pinecone doc categories):

| Operation | Description |
|-----------|-------------|
| sketch | Create 2D sketch with geometry (lines, circles, rectangles, arcs) |
| extrude | Boss/base extrusion from sketch |
| cut | Extruded cut (pocket, through-hole, slot) |
| revolve | Revolve sketch around axis |
| fillet | Add fillet to edges |
| chamfer | Add chamfer to edges |
| shell | Hollow out solid with wall thickness |
| pattern | Linear, circular, or mirror pattern |
| hole | Hole wizard (counterbore, countersink, tapped) |
| mate | Assembly mate (coincident, concentric, parallel, etc.) |

## Example Requests to Handle

1. "Create a 100mm x 50mm x 25mm rectangular block"
2. "Make a cylinder, 30mm diameter, 80mm tall, with a 10mm hole through the center"
3. "Create a flanged shaft: 20mm diameter × 100mm long with a 40mm diameter × 5mm thick flange at one end"
4. "Make a simple enclosure box: 80×60×40mm outer dimensions, 2mm wall thickness, open on top"
5. "Create a mounting plate: 100×100×5mm with four 5mm holes in a 80mm square pattern, 3mm fillets on corners"

## Error Handling

- If decomposition fails → return error message, no code
- If retrieval returns nothing → proceed with coder's training knowledge, add warning
- If validation fails after retry → return best-effort code with validation errors as comments
- If any node throws → catch, log to LangSmith, return graceful error response

---

Build this system with clean separation between nodes, proper async handling, and comprehensive LangSmith tracing. Prioritize correctness of generated VBA (especially units) over speed.
```

---

That's the full prompt. Copy-paste into Claude Code and it should scaffold the entire project. Want me to add anything specific—like test cases or example Pinecone queries?
