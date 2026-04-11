// Railway is a persistent Node.js server — no maxDuration or runtime exports needed
import { graph } from "@/lib/graph";
import type { GenerateRequest } from "@/types/vba";

export async function POST(req: Request) {
  let body: GenerateRequest;

  try {
    body = await req.json();
  } catch {
    return Response.json(
      { error: "Invalid JSON body" },
      { status: 400 }
    );
  }

  const { part_id, action, debug = false } = body;

  if (!part_id || !action) {
    return Response.json(
      { error: "part_id and action are required" },
      { status: 400 }
    );
  }

  try {
    const result = await graph.invoke({
      part_id,
      action,
      debug,
      retry_count: 0,
      part_type: null,
      part_description: null,
      pinecone_vba: null,
      pinecone_score: null,
      search_results: null,
      raw_vba: null,
      verify_error: null,
      final_vba: null,
      error_status: null,
      error_message: null,
    });

    if (result.error_status) {
      return Response.json(
        { error: result.error_message },
        { status: result.error_status }
      );
    }

    if (!result.final_vba) {
      return Response.json(
        {
          error: "VBA generation failed verification",
          detail: result.verify_error,
        },
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
