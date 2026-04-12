import { NextRequest, NextResponse } from 'next/server';
import { randomUUID } from 'crypto';
import { traceable } from 'langsmith/traceable';
import { planExecutions } from '@/lib/planner';
import { graph } from '@/lib/graph';
import type { OpenAIChatRequest, OpenAIChatResponse, OpenAIChatMessage } from '@/types/openai';

interface PipelineResult {
  content: string;
  allSuccess: boolean;
}

const runVBAPipeline = traceable(
  async (messages: OpenAIChatMessage[]): Promise<PipelineResult> => {
    // Step 1: Haiku planner — parse intent and plan execution sequence
    const plan = await planExecutions(messages);

    // Step 2: Execute VBA generation sequentially in planned order
    const sorted = [...plan.executions].sort(
      (a, b) => a.sequence_number - b.sequence_number
    );

    const results: Array<{
      execution: (typeof sorted)[number];
      vba: string | null;
      error: string | null;
      status: number;
    }> = [];

    for (const execution of sorted) {
      console.log(
        `[OpenAI Route] Executing step ${execution.sequence_number}: ${execution.part_id} — ${execution.action}`
      );
      try {
        const state = await graph.invoke({
          part_id: execution.part_id,
          action: execution.action,
          debug: false,
          retry_count: 0,
          pinecone_vba: null,
          pinecone_score: null,
          search_results: null,
          raw_vba: null,
          verify_error: null,
          final_vba: null,
        });

        if (!state.final_vba) {
          results.push({
            execution,
            vba: null,
            error: state.verify_error ?? 'VBA failed verification',
            status: 422,
          });
        } else {
          results.push({ execution, vba: state.final_vba, error: null, status: 200 });
        }
      } catch (err) {
        results.push({
          execution,
          vba: null,
          error: err instanceof Error ? err.message : 'Unknown error',
          status: 500,
        });
      }
    }

    // Step 3: Build response content
    const allSuccess = results.every((r) => r.vba !== null);
    const content =
      results.length === 1
        ? results[0].vba ??
          `Error for ${results[0].execution.part_id}: ${results[0].error}`
        : results
            .map((r) =>
              r.vba
                ? `' === [Step ${r.execution.sequence_number}] ${r.execution.description} ===\n${r.vba}`
                : `' === [Step ${r.execution.sequence_number}] ERROR: ${r.execution.part_id} — ${r.error} ===`
            )
            .join('\n\n');

    return { content, allSuccess };
  },
  { name: 'vba-pipeline' }
);

export async function POST(req: NextRequest) {
  let body: OpenAIChatRequest;
  try {
    body = await req.json();
  } catch {
    return NextResponse.json(
      { error: { message: 'Invalid JSON body', type: 'invalid_request_error' } },
      { status: 400 }
    );
  }

  if (!body.messages || body.messages.length === 0) {
    return NextResponse.json(
      {
        error: {
          message: 'messages is required and must be a non-empty array',
          type: 'invalid_request_error',
        },
      },
      { status: 400 }
    );
  }

  try {
    const { content, allSuccess } = await runVBAPipeline(body.messages);

    const response: OpenAIChatResponse = {
      id: `chatcmpl-${randomUUID()}`,
      object: 'chat.completion',
      created: Math.floor(Date.now() / 1000),
      model: body.model ?? 'node-star-vba-1',
      choices: [
        {
          index: 0,
          message: { role: 'assistant', content },
          finish_reason: allSuccess ? 'stop' : null,
        },
      ],
      usage: { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 },
    };

    return NextResponse.json(response);
  } catch (err) {
    console.error('[OpenAI Route] Unhandled error:', err);
    return NextResponse.json(
      { error: { message: 'Internal server error', type: 'api_error' } },
      { status: 500 }
    );
  }
}
