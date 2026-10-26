/* ============================================================================
 * 20_chat_data_export.sql — เก็บ Query ที่ AI เขียนไว้กับคำตอบ เพื่อให้ผู้ใช้กด "ดาวน์โหลดข้อมูลทั้งหมด"
 *
 * เมื่อผลลัพธ์ในแชทถูกตัด (จำกัดขนาดเพื่อไม่ให้ใช้ token สูง) ระบบเก็บ Query ที่รันไว้ที่ข้อความตอบ
 * ปุ่มดาวน์โหลดจะรัน Query เดิมอีกครั้งแบบไม่จำกัดแถว (สูงสุด 100,000) แล้วส่งเป็น Excel / CSV
 * — Query มาจากฐานข้อมูลนี้เท่านั้น ผู้ใช้ส่ง SQL เองเข้ามาไม่ได้
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 20_chat_data_export.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

IF COL_LENGTH('dbo.ChatMessages', 'DataExportQuery') IS NULL
BEGIN
    ALTER TABLE dbo.[ChatMessages] ADD [DataExportQuery] NVARCHAR(MAX) NULL;
    PRINT 'added ChatMessages.DataExportQuery';
END
GO
