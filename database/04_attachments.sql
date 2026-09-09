/* ============================================================
   MGT_AI_Authen - ไฟล์แนบในการแชท
   รันซ้ำได้ (idempotent)

   รันด้วย:
     sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P "<PASSWORD>" -C -f 65001 -i 04_attachments.sql

   หลักการ: ตัวไฟล์เก็บบน disk (ตาม Uploads:RootPath) ส่วนตารางนี้เก็บ metadata
   พร้อม SHA-256 เพื่อ (1) ตรวจสอบย้อนหลังว่าพนักงานส่งไฟล์อะไรออกไป
   (2) เปิดไฟล์เดิมมาเทียบได้ (3) ตรวจว่าไฟล์บน disk ยังไม่ถูกแก้ไข
   ============================================================ */

USE [MGT_AI_Authen];
GO

SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.ChatAttachments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.[ChatAttachments]
    (
        [AttachmentID]    BIGINT           IDENTITY(1,1) NOT NULL,
        [MessageID]       BIGINT           NOT NULL,

        /* เก็บ SessionID/UserID ซ้ำไว้ เพื่อค้น "ใครส่งไฟล์อะไร" ได้โดยไม่ต้อง join */
        [SessionID]       UNIQUEIDENTIFIER NOT NULL,
        [UserID]          INT              NOT NULL,

        [FileName]        NVARCHAR(300)    NOT NULL,
        [ContentType]     NVARCHAR(150)    NOT NULL,

        /* Image / Pdf / Text — กำหนดว่าจะส่งให้ Claude เป็น content block แบบใด */
        [FileKind]        NVARCHAR(20)     NOT NULL,

        [SizeBytes]       BIGINT           NOT NULL,
        [Sha256]          CHAR(64)         NOT NULL,

        /* เส้นทางแบบ relative จาก Uploads:RootPath (เก็บ relative เพื่อย้ายเครื่องได้) */
        [StoredPath]      NVARCHAR(500)    NOT NULL,

        /* ไฟล์ข้อความจะถูกอ่านเนื้อหามาคัดกรองตาม PolicyRules ได้ */
        [IsTextExtracted] BIT              NOT NULL CONSTRAINT DF_ChatAttachments_IsTextExtracted DEFAULT (0),
        [ExtractedChars]  INT              NULL,

        /* 1 = เนื้อหาไฟล์ถูกคัดกรองตาม policy แล้ว, 0 = คัดกรองได้แค่ชื่อไฟล์ (PDF/รูปภาพ) */
        [PolicyScanned]   BIT              NOT NULL CONSTRAINT DF_ChatAttachments_PolicyScanned DEFAULT (0),

        [CreatedAt]       DATETIME2(0)     NOT NULL CONSTRAINT DF_ChatAttachments_CreatedAt DEFAULT (SYSDATETIME()),

        CONSTRAINT PK_ChatAttachments PRIMARY KEY CLUSTERED ([AttachmentID]),
        CONSTRAINT FK_ChatAttachments_Messages FOREIGN KEY ([MessageID])
            REFERENCES dbo.[ChatMessages]([MessageID]) ON DELETE CASCADE,
        CONSTRAINT FK_ChatAttachments_Users FOREIGN KEY ([UserID])
            REFERENCES dbo.[Users]([UserID]),
        CONSTRAINT CK_ChatAttachments_FileKind CHECK ([FileKind] IN ('Image', 'Pdf', 'Text'))
    );

    CREATE INDEX IX_ChatAttachments_MessageID ON dbo.[ChatAttachments]([MessageID]);
    CREATE INDEX IX_ChatAttachments_UserID    ON dbo.[ChatAttachments]([UserID], [CreatedAt] DESC);
    CREATE INDEX IX_ChatAttachments_Sha256    ON dbo.[ChatAttachments]([Sha256]);
    CREATE INDEX IX_ChatAttachments_SessionID ON dbo.[ChatAttachments]([SessionID]);
END
GO

/* ธงบน ChatMessages เพื่อให้กรอง "ข้อความที่มีไฟล์แนบ" ได้เร็วโดยไม่ต้อง join */
IF COL_LENGTH('dbo.ChatMessages', 'AttachmentCount') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [AttachmentCount] INT NOT NULL
        CONSTRAINT DF_ChatMessages_AttachmentCount DEFAULT (0);
GO

/* ตั้งใจไม่ใช้ filtered index (WHERE [AttachmentCount] > 0)
 * ด้วยเหตุผลเดียวกับที่ TotalTokens ไม่ใช้ PERSISTED:
 * filtered index ทำให้ทุกคำสั่ง INSERT/UPDATE/DELETE บนตารางนี้ต้องรันด้วย QUOTED_IDENTIFIER ON
 * ไม่งั้นได้ error Msg 1934 ซึ่ง sqlcmd ตั้งเป็น OFF โดยปริยาย (พบจริงตอนทดสอบ)
 * index ปกติทำงานได้ดีพอสำหรับการกรอง AttachmentCount > 0 */
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_ChatMessages_AttachmentCount'
      AND object_id = OBJECT_ID('dbo.ChatMessages')
      AND has_filter = 1
)
BEGIN
    DROP INDEX IX_ChatMessages_AttachmentCount ON dbo.[ChatMessages];
    PRINT 'ลบ filtered index ของ AttachmentCount แล้ว (จะสร้างใหม่แบบไม่มี filter)';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_ChatMessages_AttachmentCount' AND object_id = OBJECT_ID('dbo.ChatMessages')
)
    CREATE INDEX IX_ChatMessages_AttachmentCount
        ON dbo.[ChatMessages]([AttachmentCount]);
GO

/* เหตุการณ์ใหม่ใน AuditLogs: FILE_UPLOADED, FILE_REJECTED, FILE_DOWNLOADED
   (ไม่ต้องสร้างอะไรเพิ่ม เพราะ Action เป็นคอลัมน์ข้อความอิสระอยู่แล้ว) */

PRINT 'attachments ready.';
GO
