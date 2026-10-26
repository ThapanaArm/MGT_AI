/* ============================================================================
 * 18_datasource_categories.sql — Category / Sub Category ย้ายจาก "RAG" มาเป็นของ Data Source Registry
 *
 * เดิม (15_rag_categories.sql) หมวดหมู่ผูกเฉพาะ RAG ตอนนี้ใช้จัดกลุ่ม Data Source ทุกชนิด
 * จึงเปลี่ยนชื่อให้ตรงกับความหมายใหม่ (ข้อมูลเดิมคงอยู่ครบ — เป็นการ rename ไม่ใช่สร้างใหม่):
 *   RagCategories            -> DataSourceCategories
 *   RagSubCategories         -> DataSourceSubCategories
 *   DataSources.RagCategoryId    -> CategoryId
 *   DataSources.RagSubCategoryId -> SubCategoryId
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 18_datasource_categories.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

IF OBJECT_ID(N'dbo.RagCategories', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.DataSourceCategories', N'U') IS NULL
BEGIN
    EXEC sp_rename N'dbo.RagCategories', N'DataSourceCategories';
    PRINT 'renamed RagCategories -> DataSourceCategories';
END
GO

IF OBJECT_ID(N'dbo.RagSubCategories', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.DataSourceSubCategories', N'U') IS NULL
BEGIN
    EXEC sp_rename N'dbo.RagSubCategories', N'DataSourceSubCategories';
    PRINT 'renamed RagSubCategories -> DataSourceSubCategories';
END
GO

IF COL_LENGTH('dbo.DataSources', 'RagCategoryId') IS NOT NULL
BEGIN
    EXEC sp_rename N'dbo.DataSources.RagCategoryId', N'CategoryId', N'COLUMN';
    PRINT 'renamed DataSources.RagCategoryId -> CategoryId';
END
GO

IF COL_LENGTH('dbo.DataSources', 'RagSubCategoryId') IS NOT NULL
BEGIN
    EXEC sp_rename N'dbo.DataSources.RagSubCategoryId', N'SubCategoryId', N'COLUMN';
    PRINT 'renamed DataSources.RagSubCategoryId -> SubCategoryId';
END
GO

/* ชื่อ constraint / index ให้ตรงกับชื่อตารางใหม่ (ไม่กระทบการทำงาน แต่ DBA จะไม่งง) */
IF OBJECT_ID(N'dbo.PK_RagCategories') IS NOT NULL EXEC sp_rename N'PK_RagCategories', N'PK_DataSourceCategories', N'OBJECT';
IF OBJECT_ID(N'dbo.PK_RagSubCategories') IS NOT NULL EXEC sp_rename N'PK_RagSubCategories', N'PK_DataSourceSubCategories', N'OBJECT';
IF OBJECT_ID(N'dbo.DF_RagCategories_IsActive') IS NOT NULL EXEC sp_rename N'DF_RagCategories_IsActive', N'DF_DataSourceCategories_IsActive', N'OBJECT';
IF OBJECT_ID(N'dbo.DF_RagCategories_CreatedAt') IS NOT NULL EXEC sp_rename N'DF_RagCategories_CreatedAt', N'DF_DataSourceCategories_CreatedAt', N'OBJECT';
IF OBJECT_ID(N'dbo.DF_RagSubCategories_IsActive') IS NOT NULL EXEC sp_rename N'DF_RagSubCategories_IsActive', N'DF_DataSourceSubCategories_IsActive', N'OBJECT';
IF OBJECT_ID(N'dbo.DF_RagSubCategories_CreatedAt') IS NOT NULL EXEC sp_rename N'DF_RagSubCategories_CreatedAt', N'DF_DataSourceSubCategories_CreatedAt', N'OBJECT';
IF OBJECT_ID(N'dbo.FK_RagSubCategories_RagCategories') IS NOT NULL EXEC sp_rename N'FK_RagSubCategories_RagCategories', N'FK_DataSourceSubCategories_Categories', N'OBJECT';
IF OBJECT_ID(N'dbo.FK_DataSources_RagCategories') IS NOT NULL EXEC sp_rename N'FK_DataSources_RagCategories', N'FK_DataSources_Categories', N'OBJECT';
IF OBJECT_ID(N'dbo.FK_DataSources_RagSubCategories') IS NOT NULL EXEC sp_rename N'FK_DataSources_RagSubCategories', N'FK_DataSources_SubCategories', N'OBJECT';
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RagCategories_Name' AND object_id = OBJECT_ID(N'dbo.DataSourceCategories'))
    EXEC sp_rename N'dbo.DataSourceCategories.UX_RagCategories_Name', N'UX_DataSourceCategories_Name', N'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RagSubCategories_Name' AND object_id = OBJECT_ID(N'dbo.DataSourceSubCategories'))
    EXEC sp_rename N'dbo.DataSourceSubCategories.UX_RagSubCategories_Name', N'UX_DataSourceSubCategories_Name', N'INDEX';
GO
