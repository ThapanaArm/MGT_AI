/* ============================================================================
 * 17_external_auth.sql — รองรับ login จากฐานข้อมูลผู้ใช้ภายนอก (SQL Server อีกตัว)
 *
 * Users.AuthSource บอกว่าบัญชีนี้ยืนยันตัวตนจากที่ไหน:
 *   'Local'    — รหัสผ่านเก็บในตาราง Users นี้ (บัญชีเดิม เช่น admin01)
 *   'External' — ยืนยันรหัสผ่านกับตาราง MS_User ของ SQL Server ภายนอกทุกครั้งที่ login
 *                แถวในตารางนี้เป็นแค่ "ตัวแทน" ไว้ผูกประวัติแชท/สิทธิ์ ไม่มีรหัสผ่านที่ใช้ได้จริง
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 17_external_auth.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

IF COL_LENGTH('dbo.Users', 'AuthSource') IS NULL
BEGIN
    ALTER TABLE dbo.[Users] ADD [AuthSource] NVARCHAR(10) NOT NULL
        CONSTRAINT [DF_Users_AuthSource] DEFAULT (N'Local');
    PRINT 'added Users.AuthSource';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Users_AuthSource')
BEGIN
    ALTER TABLE dbo.[Users] ADD CONSTRAINT [CK_Users_AuthSource]
        CHECK ([AuthSource] IN (N'Local', N'External'));
    PRINT 'added CK_Users_AuthSource';
END
GO
