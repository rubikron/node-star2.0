import { ChatAnthropic } from '@langchain/anthropic';
import {
  HumanMessage,
  AIMessage,
  SystemMessage,
  BaseMessage,
} from '@langchain/core/messages';
import type { OpenAIChatMessage } from '@/types/openai';

export interface PlannedExecution {
  part_id: string;
  action: string;
  sequence_number: number;
  description: string;
}

export interface ExecutionPlan {
  reasoning: string;
  executions: PlannedExecution[];
}

const PLANNER_SYSTEM = `You are a SolidWorks VBA macro planning assistant for Node-Star Company.

Your job is to parse the user's message and call create_execution_plan with a structured plan.

Rules:
- A part ID is any identifier the user mentions that refers to a part (e.g. "SW-001", "PART-123", "bracket-A", "assembly_v2"). Treat any alphanumeric token with dashes or underscores as a part ID.
- If the user mentions multiple parts, create one execution entry per part.
- If the user does not mention a specific part ID, use "UNKNOWN" as the part_id — still create the execution.
- Write action descriptions that are precise and suitable for SolidWorks VBA code generation.
- Always call create_execution_plan, even for simple single-part requests.`;

const CREATE_PLAN_TOOL = {
  name: 'create_execution_plan',
  description:
    'Create a structured execution plan for SolidWorks VBA macro generation tasks',
  input_schema: {
    type: 'object',
    properties: {
      reasoning: {
        type: 'string',
        description: 'Brief explanation of what the user wants to accomplish',
      },
      executions: {
        type: 'array',
        description:
          'Ordered list of VBA generation tasks, one entry per part-action combination',
        items: {
          type: 'object',
          properties: {
            part_id: {
              type: 'string',
              description:
                'The SolidWorks part ID to modify, exactly as stated by the user',
            },
            action: {
              type: 'string',
              description:
                'Clear, specific action description optimized for VBA code generation',
            },
            sequence_number: {
              type: 'number',
              description: 'Execution order, 1-based',
            },
            description: {
              type: 'string',
              description: 'Human-readable description of this step',
            },
          },
          required: ['part_id', 'action', 'sequence_number', 'description'],
        },
      },
    },
    required: ['reasoning', 'executions'],
  },
};

let _planner: ReturnType<typeof buildPlanner> | null = null;

function buildPlanner() {
  return new ChatAnthropic({
    model: 'claude-haiku-4-5-20251001',
    maxTokens: 1024,
  }).bindTools([CREATE_PLAN_TOOL], { tool_choice: 'any' });
}

function getPlanner() {
  if (!_planner) _planner = buildPlanner();
  return _planner;
}

function toBaseMessages(messages: OpenAIChatMessage[]): BaseMessage[] {
  return messages.map((m) => {
    if (m.role === 'system') return new SystemMessage(m.content);
    if (m.role === 'assistant') return new AIMessage(m.content);
    return new HumanMessage(m.content);
  });
}

export async function planExecutions(
  messages: OpenAIChatMessage[]
): Promise<ExecutionPlan> {
  const lcMessages = toBaseMessages(messages);
  const hasSystem = lcMessages.some((m) => m instanceof SystemMessage);
  const finalMessages = hasSystem
    ? lcMessages
    : [new SystemMessage(PLANNER_SYSTEM), ...lcMessages];

  console.log('[Planner] Sending to Haiku...');
  const response = await getPlanner().invoke(finalMessages);

  const toolCall = response.tool_calls?.[0];
  console.log('[Planner] Raw tool call:', JSON.stringify(toolCall?.args, null, 2));

  if (!toolCall || toolCall.name !== 'create_execution_plan') {
    throw new Error('[Planner] Haiku did not return a structured execution plan');
  }

  const plan = toolCall.args as ExecutionPlan;
  console.log(
    `[Planner] ${plan.executions.length} execution(s) planned — ${plan.reasoning}`
  );
  return plan;
}
