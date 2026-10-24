-- AdvancedRag schema. Idempotent: safe to run repeatedly (the API runs it on startup).

IF OBJECT_ID('dbo.Documents') IS NULL
CREATE TABLE dbo.Documents (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(200)  NOT NULL,
    FilePath        NVARCHAR(500)  NOT NULL,
    Profile         NVARCHAR(20)   NOT NULL,           -- PlainText | LongReport | Visual
    Status          NVARCHAR(20)   NOT NULL,           -- NotIngested | Ingesting | Ready | Failed
    PageCount       INT            NOT NULL DEFAULT 0,
    ChunkCount      INT            NOT NULL DEFAULT 0,
    EmbeddingModel  NVARCHAR(100)  NULL,               -- questions must be embedded with the same model
    Error           NVARCHAR(MAX)  NULL,
    CreatedAt       DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    IngestedAt      DATETIME2      NULL
);
GO

IF OBJECT_ID('dbo.Chunks') IS NULL
CREATE TABLE dbo.Chunks (
    Id           UNIQUEIDENTIFIER PRIMARY KEY,
    DocumentId   INT            NOT NULL REFERENCES dbo.Documents(Id) ON DELETE CASCADE,
    ChunkSet     NVARCHAR(20)   NOT NULL,              -- advanced | naive
    ParentId     UNIQUEIDENTIFIER NULL,                -- parent-child chunking (use case 2)
    ChunkIndex   INT            NOT NULL,
    ContentType  NVARCHAR(20)   NOT NULL,              -- text | aside | table | chart | footnote | summary
    PageNumber   INT            NOT NULL,
    PageEnd      INT            NOT NULL,
    SectionPath  NVARCHAR(500)  NOT NULL,
    Text         NVARCHAR(MAX)  NOT NULL,
    TokenCount   INT            NOT NULL,
    Embedding    VARBINARY(MAX) NULL                   -- float32[] little-endian; NULL for parent sections (never searched)
);
GO

-- Parent sections (small-to-big) are stored without an embedding.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Chunks') AND name = 'Embedding' AND is_nullable = 0)
    ALTER TABLE dbo.Chunks ALTER COLUMN Embedding VARBINARY(MAX) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Chunks_Document')
CREATE INDEX IX_Chunks_Document ON dbo.Chunks (DocumentId, ChunkSet, ChunkIndex);
GO

IF OBJECT_ID('dbo.Messages') IS NULL
CREATE TABLE dbo.Messages (
    Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    DocumentId  INT            NOT NULL REFERENCES dbo.Documents(Id) ON DELETE CASCADE,
    Role        NVARCHAR(20)   NOT NULL,               -- user | assistant
    Content     NVARCHAR(MAX)  NOT NULL,
    MetaJson    NVARCHAR(MAX)  NULL,                   -- citations + pipeline trace
    CreatedAt   DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('dbo.EvalRuns') IS NULL
CREATE TABLE dbo.EvalRuns (
    Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    DocumentId  INT            NOT NULL REFERENCES dbo.Documents(Id) ON DELETE CASCADE,
    CreatedAt   DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    ResultJson  NVARCHAR(MAX)  NOT NULL
);
GO
