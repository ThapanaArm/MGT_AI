/* ============================================================================
 * 15_rag_categories.sql — หมวดหมู่ (Category / Sub Category) สำหรับจัดกลุ่ม RAG
 *
 *   RagCategories     — หมวดหลัก เช่น "Accounting", "HR"
 *   RagSubCategories  — หมวดย่อยภายใต้หมวดหลัก เช่น "Accounting > Tax"
 *   DataSources       — เพิ่ม RagCategoryId / RagSubCategoryId (NULL ได้) ผูก RAG source เข้ากับหมวด
 *
 * ไม่ใช้ PERSISTED computed column หรือ filtered index (ทำให้ DML ทุกคำสั่งต้องการ
 * QUOTED_IDENTIFIER ON ซึ่ง sqlcmd ปิดไว้) — unique index ธรรมดาเพียงพอ
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 15_rag_categories.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'RagCategories')
BEGIN
    CREATE TABLE dbo.[RagCategories]
    (
        [CategoryId]   INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_RagCategories] PRIMARY KEY,
        [CategoryName] NVARCHAR(150)     NOT NULL,
        [Description]  NVARCHAR(500)     NULL,
        [IsActive]     BIT               NOT NULL CONSTRAINT [DF_RagCategories_IsActive] DEFAULT (1),
        [CreatedAt]    DATETIME2(0)      NOT NULL CONSTRAINT [DF_RagCategories_CreatedAt] DEFAULT (SYSDATETIME()),
        [CreatedBy]    NVARCHAR(100)     NULL,
        [UpdatedAt]    DATETIME2(0)      NULL,
        [UpdatedBy]    NVARCHAR(100)     NULL
    );
    CREATE UNIQUE INDEX [UX_RagCategories_Name] ON dbo.[RagCategories] ([CategoryName]);
    PRINT 'created RagCategories';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'RagSubCategories')
BEGIN
    CREATE TABLE dbo.[RagSubCategories]
    (
        [SubCategoryId]   INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_RagSubCategories] PRIMARY KEY,
        [CategoryId]      INT               NOT NULL,
        [SubCategoryName] NVARCHAR(150)     NOT NULL,
        [Description]     NVARCHAR(500)     NULL,
        [IsActive]        BIT               NOT NULL CONSTRAINT [DF_RagSubCategories_IsActive] DEFAULT (1),
        [CreatedAt]       DATETIME2(0)      NOT NULL CONSTRAINT [DF_RagSubCategories_CreatedAt] DEFAULT (SYSDATETIME()),
        [CreatedBy]       NVARCHAR(100)     NULL,
        [UpdatedAt]       DATETIME2(0)      NULL,
        [UpdatedBy]       NVARCHAR(100)     NULL,
        CONSTRAINT [FK_RagSubCategories_RagCategories] FOREIGN KEY ([CategoryId])
            REFERENCES dbo.[RagCategories] ([CategoryId])
    );
    CREATE UNIQUE INDEX [UX_RagSubCategories_Name] ON dbo.[RagSubCategories] ([CategoryId], [SubCategoryName]);
    PRINT 'created RagSubCategories';
END
GO

IF COL_LENGTH('dbo.DataSources', 'RagCategoryId') IS NULL
BEGIN
    ALTER TABLE dbo.[DataSources] ADD
        [RagCategoryId]    INT NULL,
        [RagSubCategoryId] INT NULL;
    PRINT 'added DataSources.RagCategoryId / RagSubCategoryId';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_DataSources_RagCategories')
BEGIN
    ALTER TABLE dbo.[DataSources] ADD CONSTRAINT [FK_DataSources_RagCategories]
        FOREIGN KEY ([RagCategoryId]) REFERENCES dbo.[RagCategories] ([CategoryId]);
    ALTER TABLE dbo.[DataSources] ADD CONSTRAINT [FK_DataSources_RagSubCategories]
        FOREIGN KEY ([RagSubCategoryId]) REFERENCES dbo.[RagSubCategories] ([SubCategoryId]);
    PRINT 'added DataSources category foreign keys';
END
GO
