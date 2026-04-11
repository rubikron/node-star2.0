CREATE TABLE IF NOT EXISTS parts (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  part_id     VARCHAR(100) UNIQUE NOT NULL,
  part_name   VARCHAR(255) NOT NULL,
  part_type   VARCHAR(100) NOT NULL,   -- e.g. "sheet_metal", "extrusion", "fastener"
  description TEXT,
  is_active   BOOLEAN DEFAULT TRUE,
  created_at  TIMESTAMPTZ DEFAULT NOW()
);
