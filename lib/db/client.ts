import { neon } from "@neondatabase/serverless";
import type { NeonQueryFunction } from "@neondatabase/serverless";

// Lazy singleton — initialized on first call so Next.js build doesn't throw
let _sql: NeonQueryFunction<false, false> | null = null;

function getSql(): NeonQueryFunction<false, false> {
  if (!_sql) {
    if (!process.env.DATABASE_URL) {
      throw new Error("DATABASE_URL environment variable is required");
    }
    _sql = neon(process.env.DATABASE_URL);
  }
  return _sql;
}

export interface PartRecord {
  part_type: string;
  description: string | null;
}

/**
 * Look up an active part by its part_id.
 * Returns null if not found or inactive.
 */
export async function getPartByPartId(partId: string): Promise<PartRecord | null> {
  const sql = getSql();
  const rows = await sql`
    SELECT part_type, description
    FROM parts
    WHERE part_id = ${partId}
      AND is_active = TRUE
    LIMIT 1
  `;

  if (rows.length === 0) return null;

  return {
    part_type: rows[0].part_type as string,
    description: rows[0].description as string | null,
  };
}
