/* ============================================================================
 * 16_rag_runs.sql — ประวัติการ "Create RAG" แต่ละครั้ง พร้อมจำนวน token และค่าใช้จ่าย
 *
 * ไม่มี FK ไป DataSources โดยตั้งใจ: ลบ RAG แล้วประวัติค่าใช้จ่ายต้องยังอยู่ (เก็บชื่อไว้ใน SourceName)
 * ค่าใช้จ่ายถูกคำนวณและ "ตรึง" ตอนจบงาน (เหมือน ChatMessages) ราคาที่แก้ภายหลังไม่ย้อนมาเปลี่ยน
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 16_rag_runs.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'RagRuns')
BEGIN
    CREATE TABLE dbo.[RagRuns]
    (
        [RunId]              UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_RagRuns] PRIMARY KEY,
        [SourceId]           INT              NULL,
        [SourceName]         NVARCHAR(150)    NOT NULL,
        [Model]              NVARCHAR(100)    NOT NULL,
        [Provider]           NVARCHAR(30)     NOT NULL,
        [State]              NVARCHAR(20)     NOT NULL,
        [FilesTotal]         INT              NOT NULL,
        [PagesTotal]         INT              NOT NULL,
        [ChunksCreated]      INT              NOT NULL CONSTRAINT [DF_RagRuns_Chunks] DEFAULT (0),
        [InputTokens]        BIGINT           NOT NULL CONSTRAINT [DF_RagRuns_In] DEFAULT (0),
        [OutputTokens]       BIGINT           NOT NULL CONSTRAINT [DF_RagRuns_Out] DEFAULT (0),
        [CacheTokens]        BIGINT           NOT NULL CONSTRAINT [DF_RagRuns_Cache] DEFAULT (0),
        [EmbeddingTokens]    BIGINT           NOT NULL CONSTRAINT [DF_RagRuns_Emb] DEFAULT (0),
        [OcrCostUsd]         DECIMAL(18,8)    NOT NULL CONSTRAINT [DF_RagRuns_OcrUsd] DEFAULT (0),
        [EmbeddingCostUsd]   DECIMAL(18,8)    NOT NULL CONSTRAINT [DF_RagRuns_EmbUsd] DEFAULT (0),
        [TotalCostUsd]       DECIMAL(18,8)    NOT NULL CONSTRAINT [DF_RagRuns_TotUsd] DEFAULT (0),
        [TotalCostThb]       DECIMAL(18,6)    NOT NULL CONSTRAINT [DF_RagRuns_TotThb] DEFAULT (0),
        [UsdToThbRate]       DECIMAL(18,6)    NOT NULL CONSTRAINT [DF_RagRuns_Rate] DEFAULT (0),
        [Error]              NVARCHAR(1000)   NULL,
        [StartedBy]          NVARCHAR(100)    NULL,
        [StartedAt]          DATETIME2(0)     NOT NULL,
        [FinishedAt]         DATETIME2(0)     NULL
    );
    CREATE INDEX [IX_RagRuns_StartedAt] ON dbo.[RagRuns] ([StartedAt] DESC);
    PRINT 'created RagRuns';
END
GO
