/* ============================================================================
 * 14_rag_datasource.sql — เพิ่มชนิด Data Source ใหม่ "Rag": ฐานความรู้ในเครื่องที่ค้นด้วย
 * ความคล้ายเชิงความหมาย (semantic similarity) ของ embedding แทนการยิง query ไปยังระบบภายนอก
 * แบบ Api/DataLake/SharePoint — ใช้กับเอกสารที่ตัด chunk + สร้าง embedding ไว้ล่วงหน้าแล้ว
 * (เช่น ชุด MGT_Accounting_Knowledge ที่ผู้ใช้เตรียมมาจากอีกเซสชันหนึ่ง)
 *
 * RagChunks เก็บ embedding เป็น VARBINARY (float32 เรียงต่อกันแบบ little-endian) ไม่ใช่
 * native VECTOR type ของ SQL Server — เพราะ VECTOR/VECTOR_DISTANCE เพิ่งมีใน SQL Server 2025
 * เท่านั้น ระบบนี้ยังรันบน SQL Server 2022 จึงคำนวณ cosine similarity ฝั่ง C# แทน (จำนวน
 * chunk ต่อแหล่งข้อมูลหนึ่งอยู่ในหลักร้อย ไม่ใช่ล้าน — brute-force ในหน่วยความจำเร็วพอ ไม่ต้อง
 * ใช้ vector index)
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 14_rag_datasource.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

/* ---------------------------------------------------------------- 1. ขยาย CHECK constraint */

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_DataSources_SourceType')
BEGIN
    ALTER TABLE dbo.[DataSources] DROP CONSTRAINT [CK_DataSources_SourceType];
    PRINT 'dropped old CK_DataSources_SourceType';
END
GO

ALTER TABLE dbo.[DataSources]
    ADD CONSTRAINT [CK_DataSources_SourceType]
    CHECK ([SourceType] IN (N'Api', N'DataLake', N'LocalFolder', N'SharePoint', N'Rag'));
PRINT 'CK_DataSources_SourceType now allows Rag';
GO

/* ---------------------------------------------------------------- 2. RagChunks */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'RagChunks')
BEGIN
    CREATE TABLE dbo.[RagChunks]
    (
        [ChunkId]            BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [SourceId]           INT NOT NULL,

        /* chunk_id ของคอร์ปัสต้นฉบับ (เช่น "MGT_Accounting_Knowledge-00001") — เก็บไว้สืบย้อน
         * กลับไปยังไฟล์ต้นฉบับเท่านั้น ไม่ได้ใช้ lookup */
        [ChunkKey]           NVARCHAR(100)  NOT NULL,

        [ChunkText]          NVARCHAR(MAX)  NOT NULL,
        [SourceFile]         NVARCHAR(260)  NULL,
        [PageStart]          INT NULL,
        [PageEnd]            INT NULL,
        [Chapter]            NVARCHAR(500)  NULL,
        [Section]            NVARCHAR(500)  NULL,
        [TokenCountEstimate] INT NULL,

        /* float32 เรียงต่อกัน little-endian — ดูเหตุผลที่ไม่ใช้ native VECTOR type ด้านบน */
        [Embedding]          VARBINARY(MAX) NOT NULL,
        [EmbeddingModel]     NVARCHAR(100)  NOT NULL,

        [CreatedAt]          DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),

        CONSTRAINT [FK_RagChunks_DataSources]
            FOREIGN KEY ([SourceId]) REFERENCES dbo.[DataSources]([SourceId])
            ON DELETE CASCADE
    );

    CREATE INDEX [IX_RagChunks_SourceId] ON dbo.[RagChunks]([SourceId]);

    PRINT 'created table RagChunks';
END
ELSE
BEGIN
    PRINT 'table RagChunks already exists — skipped';
END
GO

PRINT 'rag datasource type ready.';
GO
