/* ============================================================================
 * 12_datasource_chat.sql — ต่อทะเบียนแหล่งข้อมูล (10_data_sources.sql) เข้ากับแชทจริง
 *
 * Phase 2 ตามที่ตกลง: เลือก Data Source ตอนเริ่มบทสนทนาใหม่ (แบบเดียวกับ Project) —
 * ระบบดึงข้อมูลครั้งเดียวตอนเริ่มแชท แล้วแคชผลไว้ที่ ChatSessionDataFetches หนึ่งแถวต่อ
 * หนึ่งบทสนทนา ผู้ใช้กด "Refresh" เพื่อดึงใหม่ได้เอง (เขียนทับแถวเดิม)
 *
 * การกรองสิทธิ์: ใช้ DataSourceGrants.ScopeFilter ตรง ๆ เป็นตัวจำกัดข้อมูลที่ดึง
 * (path ย่อยสำหรับ LocalFolder/SharePoint, query string ต่อท้ายสำหรับ Api) — ไม่มี
 * automation ผูกกับ Department โดยอัตโนมัติ แอดมินที่ต้องการจำกัดตาม Division ต้อง
 * เขียนเงื่อนไขนั้นลง ScopeFilter เอง (ตามที่ตกลงไว้)
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 12_datasource_chat.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

/* ---------------------------------------------------------------- 1. ChatSessions.DataSourceId
 *
 * ผูกบทสนทนาหนึ่งเข้ากับ Data Source หนึ่ง (ตอนสร้างบทสนทนาใหม่เท่านั้น เปลี่ยนทีหลังไม่ได้
 * — เหตุผลเดียวกับ ChatSessions.ProjectId) */

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'dbo.ChatSessions') AND [name] = N'DataSourceId')
BEGIN
    ALTER TABLE dbo.[ChatSessions] ADD [DataSourceId] INT NULL;

    ALTER TABLE dbo.[ChatSessions]
        ADD CONSTRAINT [FK_ChatSessions_DataSources] FOREIGN KEY ([DataSourceId])
        REFERENCES dbo.[DataSources]([SourceId]);

    CREATE INDEX [IX_ChatSessions_DataSource] ON dbo.[ChatSessions]([DataSourceId]);

    PRINT 'added ChatSessions.DataSourceId';
END
ELSE
BEGIN
    PRINT 'ChatSessions.DataSourceId already exists — skipped';
END
GO

/* ---------------------------------------------------------------- 2. ChatSessionDataFetches
 *
 * หนึ่งแถวต่อหนึ่งบทสนทนา (SessionId เป็น PK ตรง ๆ ไม่ใช้ identity แยก) — ดึงครั้งแรกตอน
 * เริ่มแชท และ "Refresh" คือ UPDATE แถวเดิม ไม่ใช่ INSERT ซ้ำ จึงไม่ต้องเก็บประวัติการดึง
 * แยก (audit log มี audit action DATASOURCE_FETCHED / DATASOURCE_FETCH_FAILED ให้ไล่ย้อนอยู่แล้ว) */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ChatSessionDataFetches')
BEGIN
    CREATE TABLE dbo.[ChatSessionDataFetches]
    (
        [SessionId]       UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [SourceId]        INT NOT NULL,

        [FetchedAt]       DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),
        [FetchedByUserId] INT NOT NULL,

        [Success]         BIT NOT NULL,
        [Message]         NVARCHAR(1000) NOT NULL,

        /* NULL เมื่อ Success = 0 — ไม่มีอะไรให้ส่งต่อให้ AI */
        [ContentText]     NVARCHAR(MAX) NULL,
        [CharCount]       INT NOT NULL DEFAULT 0,
        [Truncated]       BIT NOT NULL DEFAULT 0,

        CONSTRAINT [FK_ChatSessionDataFetches_Sessions] FOREIGN KEY ([SessionId])
            REFERENCES dbo.[ChatSessions]([SessionId]),
        CONSTRAINT [FK_ChatSessionDataFetches_Sources] FOREIGN KEY ([SourceId])
            REFERENCES dbo.[DataSources]([SourceId]),
        CONSTRAINT [FK_ChatSessionDataFetches_Users] FOREIGN KEY ([FetchedByUserId])
            REFERENCES dbo.[Users]([UserID])
    );

    PRINT 'created table ChatSessionDataFetches';
END
ELSE
BEGIN
    PRINT 'table ChatSessionDataFetches already exists — skipped';
END
GO

PRINT 'data source chat integration ready.';
GO
