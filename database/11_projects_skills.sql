/* ============================================================================
 * 11_projects_skills.sql — Project (พื้นที่ทำงาน + คำสั่งประจำ + ไฟล์อ้างอิง)
 * และ Skill (คลัง prompt ส่วนตัวที่แทรกลงช่องพิมพ์ได้) ที่ผู้ใช้สร้างเองได้
 *
 * ต่างจากทะเบียนแหล่งข้อมูล (10_data_sources.sql) ตรงที่หน้านี้เป็น self-service
 * — พนักงานทุกคนสร้าง/แก้/ลบของตัวเองได้ ไม่ใช่ Admin เท่านั้น และเลือกแชร์กับ
 * ทั้งองค์กรได้ (เห็น/ใช้ได้ทุกคน แต่แก้ไขได้เฉพาะเจ้าของหรือ Admin)
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 11_projects_skills.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

/* ---------------------------------------------------------------- 1. Projects */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'Projects')
BEGIN
    CREATE TABLE dbo.[Projects]
    (
        [ProjectId]      INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Name]           NVARCHAR(150)  NOT NULL,

        /* คำสั่งประจำ — ต่อท้าย system prompt ของทุกข้อความในบทสนทนาที่อยู่ใต้ Project นี้
         * (แบบเดียวกับ ChartSystemPrompt/ReportSystemPrompt ที่ต่อท้าย system prompt อยู่แล้ว) */
        [Instructions]   NVARCHAR(MAX)  NULL,

        [OwnerUserId]    INT NOT NULL,

        /* true = ทุกคนเห็นและเริ่มบทสนทนาใต้ Project นี้ได้ แต่แก้ไข/เพิ่มไฟล์ได้แค่เจ้าของกับ Admin */
        [IsShared]       BIT NOT NULL DEFAULT 0,

        /* ซ่อนไม่ลบ — บทสนทนาเดิมที่อยู่ใต้ Project นี้ต้องยังเปิดดูได้เพื่อการตรวจสอบ
         * (แบบเดียวกับ ChatSessions.IsDeleted) */
        [IsDeleted]      BIT NOT NULL DEFAULT 0,

        [CreatedAt]      DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),
        [UpdatedAt]      DATETIME2(0) NULL,

        CONSTRAINT [FK_Projects_Owner] FOREIGN KEY ([OwnerUserId])
            REFERENCES dbo.[Users]([UserID])
    );

    CREATE INDEX [IX_Projects_Owner] ON dbo.[Projects]([OwnerUserId], [IsDeleted]);

    PRINT 'created table Projects';
END
ELSE
BEGIN
    PRINT 'table Projects already exists — skipped';
END
GO

/* ---------------------------------------------------------------- 2. ProjectFiles
 *
 * ไฟล์อ้างอิงระดับ Project (ต่างจาก ChatAttachments ที่ผูกกับข้อความเดียว) — ถูกส่งซ้ำ
 * ให้ AI ทุกข้อความในทุกบทสนทนาใต้ Project นี้ ผ่านกลไก TurnAttachment เดิมที่มีอยู่แล้ว
 * (มี prompt caching อยู่แล้วจากฟีเจอร์ไฟล์แนบ จึงไม่ใช่ต้นทุนใหม่) */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ProjectFiles')
BEGIN
    CREATE TABLE dbo.[ProjectFiles]
    (
        [ProjectFileId]   BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [ProjectId]       INT NOT NULL,
        [FileName]        NVARCHAR(260) NOT NULL,
        [ContentType]     NVARCHAR(150) NOT NULL,
        [FileKind]        NVARCHAR(20)  NOT NULL,   -- Image | Pdf | Text | Spreadsheet
        [SizeBytes]       BIGINT NOT NULL,
        [Sha256]          CHAR(64) NOT NULL,
        [StoredPath]      NVARCHAR(500) NOT NULL,
        [IsTextExtracted] BIT NOT NULL DEFAULT 0,
        [ExtractedChars]  INT NULL,

        /* false = คัดกรอง policy ได้แค่ชื่อไฟล์ (PDF/รูปภาพ) — เหมือน ChatAttachments */
        [PolicyScanned]   BIT NOT NULL DEFAULT 0,

        [UserId]          INT NOT NULL,   -- ใครเป็นคนอัพโหลด
        [CreatedAt]       DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),

        CONSTRAINT [FK_ProjectFiles_Projects] FOREIGN KEY ([ProjectId])
            REFERENCES dbo.[Projects]([ProjectId]),
        CONSTRAINT [FK_ProjectFiles_Users] FOREIGN KEY ([UserId])
            REFERENCES dbo.[Users]([UserID]),
        CONSTRAINT [CK_ProjectFiles_FileKind]
            CHECK ([FileKind] IN (N'Image', N'Pdf', N'Text', N'Spreadsheet'))
    );

    CREATE INDEX [IX_ProjectFiles_Project] ON dbo.[ProjectFiles]([ProjectId]);

    PRINT 'created table ProjectFiles';
END
ELSE
BEGIN
    PRINT 'table ProjectFiles already exists — skipped';
END
GO

/* ---------------------------------------------------------------- 3. Skills */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'Skills')
BEGIN
    CREATE TABLE dbo.[Skills]
    (
        [SkillId]      INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Name]         NVARCHAR(150)  NOT NULL,

        /* ข้อความที่จะถูกแทรกลงช่องพิมพ์เมื่อกดใช้ — ไม่ใช่ system prompt และไม่ถูกส่งให้ AI
         * โดยอัตโนมัติ ผู้ใช้ยังเป็นคนกด "ส่ง" เองเสมอ */
        [Body]         NVARCHAR(MAX)  NOT NULL,

        [OwnerUserId]  INT NOT NULL,
        [IsShared]     BIT NOT NULL DEFAULT 0,
        [IsDeleted]    BIT NOT NULL DEFAULT 0,

        [CreatedAt]    DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),
        [UpdatedAt]    DATETIME2(0) NULL,

        CONSTRAINT [FK_Skills_Owner] FOREIGN KEY ([OwnerUserId])
            REFERENCES dbo.[Users]([UserID])
    );

    CREATE INDEX [IX_Skills_Owner] ON dbo.[Skills]([OwnerUserId], [IsDeleted]);

    PRINT 'created table Skills';
END
ELSE
BEGIN
    PRINT 'table Skills already exists — skipped';
END
GO

/* ---------------------------------------------------------------- 4. ChatSessions.ProjectId
 *
 * ผูกบทสนทนาหนึ่งเข้ากับ Project หนึ่ง (ตอนสร้างบทสนทนาใหม่เท่านั้น เปลี่ยนทีหลังไม่ได้
 * เพื่อไม่ให้ประวัติก่อน/หลังเปลี่ยนสับสนว่าข้อความไหนได้รับคำสั่ง/ไฟล์ของ Project ไหน) */

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'dbo.ChatSessions') AND [name] = N'ProjectId')
BEGIN
    ALTER TABLE dbo.[ChatSessions] ADD [ProjectId] INT NULL;

    ALTER TABLE dbo.[ChatSessions]
        ADD CONSTRAINT [FK_ChatSessions_Projects] FOREIGN KEY ([ProjectId])
        REFERENCES dbo.[Projects]([ProjectId]);

    CREATE INDEX [IX_ChatSessions_Project] ON dbo.[ChatSessions]([ProjectId]);

    PRINT 'added ChatSessions.ProjectId';
END
ELSE
BEGIN
    PRINT 'ChatSessions.ProjectId already exists — skipped';
END
GO

PRINT 'projects and skills ready.';
GO
