export interface GenerateRequest {
  part_id: string;
  action: string;
  debug?: boolean;
}

export interface PineconeVBAMetadata {
  part_type: string;
  action: string;
  vba_code: string;
  description: string;
}

export interface VBAVerifyResult {
  valid: boolean;
  error?: string;
}
