import * as dotenv from "dotenv";
dotenv.config();

function requireEnv(key: string): string {
  const val = process.env[key];
  if (!val) throw new Error(`Missing required environment variable: ${key}`);
  return val;
}

export const config = {
  OPENAI_API_KEY: requireEnv("OPENAI_API_KEY"),
  PINECONE_API_KEY: requireEnv("PINECONE_API_KEY"),
  PINECONE_INDEX: process.env.PINECONE_INDEX ?? "solidworks-api-code",
  LANGSMITH_API_KEY: requireEnv("LANGSMITH_API_KEY"),
  LANGSMITH_PROJECT: process.env.LANGSMITH_PROJECT ?? "solidworks-vba-gen",
  PORT: parseInt(process.env.PORT ?? "8000", 10),
};

// LangSmith picks these up automatically
process.env.LANGCHAIN_TRACING_V2 = "true";
process.env.LANGCHAIN_API_KEY = config.LANGSMITH_API_KEY;
process.env.LANGCHAIN_PROJECT = config.LANGSMITH_PROJECT;
