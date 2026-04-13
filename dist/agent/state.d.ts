import { BaseMessage } from "@langchain/core/messages";
export interface Step {
    step_number: number;
    operation: "sketch" | "extrude" | "cut" | "revolve" | "fillet" | "chamfer" | "shell" | "pattern" | "hole" | "mate";
    description: string;
    parameters: Record<string, unknown>;
}
export interface ValidationResult {
    is_valid: boolean;
    errors: string[];
    corrected_code: string;
}
export interface GraphState {
    messages: BaseMessage[];
    steps: Step[];
    api_docs: string[];
    vba_code: string;
    validation: ValidationResult;
    retry_count: number;
}
export declare const GraphStateAnnotation: import("@langchain/langgraph").AnnotationRoot<{
    messages: import("@langchain/langgraph").BinaryOperatorAggregate<BaseMessage[], BaseMessage[]>;
    steps: import("@langchain/langgraph").BinaryOperatorAggregate<Step[], Step[]>;
    api_docs: import("@langchain/langgraph").BinaryOperatorAggregate<string[], string[]>;
    vba_code: import("@langchain/langgraph").BinaryOperatorAggregate<string, string>;
    validation: import("@langchain/langgraph").BinaryOperatorAggregate<ValidationResult, ValidationResult>;
    retry_count: import("@langchain/langgraph").BinaryOperatorAggregate<number, number>;
}>;
//# sourceMappingURL=state.d.ts.map