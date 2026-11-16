-- Adding a column with a default keeps the previous version of the app working.
ALTER TABLE todos ADD COLUMN updated_at TIMESTAMPTZ NOT NULL DEFAULT now();

-- The cleanup job looks for old done todos.
CREATE INDEX todos_done_updated_at_idx ON todos (updated_at) WHERE done;
