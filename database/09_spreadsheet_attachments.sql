/* ============================================================================
 * 09_spreadsheet_attachments.sql — อนุญาตไฟล์แนบชนิด Excel (.xlsx/.xls)
 *
 * ChatAttachments.FileKind มี CHECK constraint จำกัดไว้แค่ Image/Pdf/Text
 * (ตั้งไว้ใน 04_attachments.sql) การเพิ่มชนิด Spreadsheet ในโค้ดจึงยัง INSERT ไม่ผ่าน
 * จนกว่าจะขยาย constraint นี้
 *
 * บทเรียนที่เจอสองครั้งติด: ตอนขยายชุดค่าที่อนุญาต (enum-like) ต้องไล่หา
 * **ทุกสำเนาของรายการเดิม** ทั้งในโค้ด C#, validation attribute, ฝั่ง frontend
 * และ CHECK constraint ในฐานข้อมูล — ครั้งก่อนตกที่ attribute ครั้งนี้ตกที่ constraint
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 09_spreadsheet_attachments.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_ChatAttachments_FileKind')
BEGIN
    ALTER TABLE dbo.[ChatAttachments] DROP CONSTRAINT [CK_ChatAttachments_FileKind];
    PRINT 'dropped old CK_ChatAttachments_FileKind';
END
GO

ALTER TABLE dbo.[ChatAttachments]
    ADD CONSTRAINT [CK_ChatAttachments_FileKind]
    CHECK ([FileKind] IN (N'Image', N'Pdf', N'Text', N'Spreadsheet'));
PRINT 'CK_ChatAttachments_FileKind now allows Spreadsheet';
GO

SELECT c.[name] AS ConstraintName,
       OBJECT_NAME(c.parent_object_id) AS TableName,
       c.[definition] AS Definition
FROM sys.check_constraints c
WHERE c.[name] = N'CK_ChatAttachments_FileKind';
GO

PRINT 'spreadsheet attachments allowed.';
GO
