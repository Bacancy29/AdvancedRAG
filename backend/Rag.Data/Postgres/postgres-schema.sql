-- AdvancedRag schema for PostgreSQL + pgvector. Idempotent: the API runs it on startup.
-- {DIMENSIONS} is replaced with the embedding size (768 for nomic-embed-text).

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS documents (
    id              INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name            TEXT        NOT NULL,
    file_path       TEXT        NOT NULL,
    profile         TEXT        NOT NULL,              -- PlainText | LongReport | Visual
    status          TEXT        NOT NULL,              -- NotIngested | Ingesting | Ready | Failed
    page_count      INT         NOT NULL DEFAULT 0,
    chunk_count     INT         NOT NULL DEFAULT 0,
    embedding_model TEXT        NULL,
    error           TEXT        NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    ingested_at     TIMESTAMPTZ NULL
);

CREATE TABLE IF NOT EXISTS chunks (
    id           UUID PRIMARY KEY,
    document_id  INT         NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    chunk_set    TEXT        NOT NULL,                 -- advanced | naive
    parent_id    UUID        NULL,
    chunk_index  INT         NOT NULL,
    content_type TEXT        NOT NULL,
    page_number  INT         NOT NULL,
    page_end     INT         NOT NULL,
    section_path TEXT        NOT NULL,
    text         TEXT        NOT NULL,
    token_count  INT         NOT NULL,
    embedding    vector({DIMENSIONS}) NULL             -- native pgvector type; NULL for parent sections
);

ALTER TABLE chunks ALTER COLUMN embedding DROP NOT NULL;

CREATE INDEX IF NOT EXISTS ix_chunks_document ON chunks (document_id, chunk_set, chunk_index);

-- HNSW: approximate nearest-neighbour index for cosine distance (the <=> operator).
-- m / ef_construction trade build time and memory for recall; these are pgvector's defaults.
CREATE INDEX IF NOT EXISTS ix_chunks_embedding_hnsw ON chunks
    USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);

CREATE TABLE IF NOT EXISTS messages (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id INT         NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    role        TEXT        NOT NULL,
    content     TEXT        NOT NULL,
    meta_json   TEXT        NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS eval_runs (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id INT         NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    result_json TEXT        NOT NULL
);
