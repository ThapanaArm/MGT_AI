/* ============================================================================
 * 19_supabase_datasource.sql — เพิ่มชนิด Data Source ใหม่ "Supabase"
 *
 * ต่อกับโปรเจกต์ Supabase ได้ 2 แบบ (เลือกด้วย config "connectionMode"):
 *   Sql  — ต่อฐานข้อมูล Postgres ของโปรเจกต์ตรง ๆ แล้วรัน SQL query (SELECT ได้หลายคำสั่ง)
 *   Rest — เรียก REST API ของโปรเจกต์ (PostgREST) เช่น customers?select=*&status=eq.active
 *
 * ตารางเดิมไม่ต้องเปลี่ยน — ค่าเชื่อมต่อเก็บใน DataSources.ConfigJson และรหัสผ่าน/API key เก็บเข้ารหัส
 * ใน EncryptedSecret เหมือนชนิดอื่น แค่ขยาย CHECK constraint ของ SourceType ให้รับ 'Supabase'
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 19_supabase_datasource.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_DataSources_SourceType')
BEGIN
    ALTER TABLE dbo.[DataSources] DROP CONSTRAINT [CK_DataSources_SourceType];
    PRINT 'dropped old CK_DataSources_SourceType';
END
GO

ALTER TABLE dbo.[DataSources]
    ADD CONSTRAINT [CK_DataSources_SourceType]
    CHECK ([SourceType] IN (N'Api', N'DataLake', N'LocalFolder', N'SharePoint', N'Rag', N'Supabase'));
PRINT 'CK_DataSources_SourceType now allows Supabase';
GO
