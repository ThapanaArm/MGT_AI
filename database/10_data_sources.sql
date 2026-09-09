/* ============================================================================
 * 10_data_sources.sql — ทะเบียนแหล่งข้อมูลภายนอก + สิทธิ์การเข้าถึงรายผู้ใช้
 *
 * Phase 1 ตามที่ตกลง: "ทำทะเบียน + สิทธิ์ก่อน ยังไม่ต่อกับแชท"
 * Admin/IT ลงทะเบียนแหล่งข้อมูล (API, Data Lake/Lakehouse, โฟลเดอร์ในเครื่อง/SharePoint)
 * แล้วกำหนดว่าพนักงานคนไหนอ่านได้แค่ไหน — ยังไม่มีจุดใดในแชทที่ดึงข้อมูลจริง
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 10_data_sources.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

/* ---------------------------------------------------------------- 1. DataSources */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'DataSources')
BEGIN
    CREATE TABLE dbo.[DataSources]
    (
        [SourceId]           INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [SourceName]         NVARCHAR(150)  NOT NULL,
        [SourceType]         NVARCHAR(20)   NOT NULL,   -- Api | DataLake | LocalFolder | SharePoint
        [Description]        NVARCHAR(500)  NULL,

        /* ค่าที่ไม่ใช่ความลับ — URL, path, tenant id ฯลฯ เก็บเป็น JSON ดิบ
         * รูปแบบต่างกันตาม SourceType (ดูคอมเมนต์ใน DataSourceConfig.cs) */
        [ConfigJson]         NVARCHAR(MAX)  NULL,

        /* client secret / API key / SharePoint app secret — เข้ารหัสด้วย ASP.NET Core
         * Data Protection ก่อนเก็บเสมอ ไม่เคยเก็บเป็น plain text
         * (ดูเหตุผลในหัวข้อ 6.22 ของ README) */
        [EncryptedSecret]    NVARCHAR(MAX)  NULL,

        [IsActive]           BIT NOT NULL DEFAULT 1,

        [CreatedAt]          DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),
        [CreatedBy]          NVARCHAR(100) NULL,
        [UpdatedAt]          DATETIME2(0) NULL,
        [UpdatedBy]          NVARCHAR(100) NULL,

        CONSTRAINT [CK_DataSources_SourceType]
            CHECK ([SourceType] IN (N'Api', N'DataLake', N'LocalFolder', N'SharePoint')),
        CONSTRAINT [UQ_DataSources_SourceName] UNIQUE ([SourceName])
    );

    CREATE INDEX [IX_DataSources_SourceType] ON dbo.[DataSources]([SourceType], [IsActive]);

    PRINT 'created table DataSources';
END
ELSE
BEGIN
    PRINT 'table DataSources already exists — skipped';
END
GO

/* ---------------------------------------------------------------- 2. DataSourceGrants */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'DataSourceGrants')
BEGIN
    CREATE TABLE dbo.[DataSourceGrants]
    (
        [GrantId]            BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [SourceId]           INT NOT NULL,
        [UserId]             INT NOT NULL,

        /* Full = อ่านได้ทั้งหมดที่แหล่งข้อมูลนั้นมี
         * OwnDepartment = ผูกกับ Users.Department ของพนักงานคนนั้นอัตโนมัติ
         *                 (เคส Sales Manager เห็นเฉพาะ Division ตัวเอง — ไม่ต้องพิมพ์ค่าซ้ำทีละคน)
         * Custom = แอดมินพิมพ์เงื่อนไขเอง (เคส CFO อ่าน SAP API เฉพาะบาง endpoint/field) */
        [ScopeType]          NVARCHAR(20) NOT NULL DEFAULT N'Full',
        [ScopeFilter]        NVARCHAR(1000) NULL,   -- ใช้เมื่อ ScopeType = Custom เท่านั้น
        [Notes]              NVARCHAR(500) NULL,

        [GrantedAt]          DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),
        [GrantedBy]          NVARCHAR(100) NULL,

        /* เพิกถอนแล้วไม่ลบแถวทิ้ง — ตามหลักเดียวกับ PolicyRules ที่ปิดใช้งานไม่ลบ
         * เพื่อให้ audit ย้อนดูได้ว่าใครเคยให้สิทธิ์ใครไว้ตอนไหน */
        [IsActive]           BIT NOT NULL DEFAULT 1,
        [RevokedAt]          DATETIME2(0) NULL,
        [RevokedBy]          NVARCHAR(100) NULL,

        CONSTRAINT [FK_DataSourceGrants_Sources] FOREIGN KEY ([SourceId])
            REFERENCES dbo.[DataSources]([SourceId]),
        CONSTRAINT [FK_DataSourceGrants_Users] FOREIGN KEY ([UserId])
            REFERENCES dbo.[Users]([UserID]),
        CONSTRAINT [CK_DataSourceGrants_ScopeType]
            CHECK ([ScopeType] IN (N'Full', N'OwnDepartment', N'Custom'))
    );

    CREATE INDEX [IX_DataSourceGrants_User]   ON dbo.[DataSourceGrants]([UserId], [IsActive]);
    CREATE INDEX [IX_DataSourceGrants_Source] ON dbo.[DataSourceGrants]([SourceId], [IsActive]);

    /* หนึ่งคนมีสิทธิ์ที่ "เปิดอยู่" ต่อแหล่งข้อมูลหนึ่งได้แค่แถวเดียว กันสิทธิ์ซ้ำซ้อนที่ตีความยาก
     * (filtered unique index ไม่ใช่ filtered ทั่วไปที่เคยมีปัญหา QUOTED_IDENTIFIER — นี่คือ
     * unique constraint ซึ่งไม่ตกอยู่ในปัญหาเดียวกับ computed column/filtered index ธรรมดา
     * แต่เพื่อความปลอดภัยจึงเลี่ยงไว้ก่อน ใช้ตรวจซ้ำในโค้ดแทน) */

    PRINT 'created table DataSourceGrants';
END
ELSE
BEGIN
    PRINT 'table DataSourceGrants already exists — skipped';
END
GO

PRINT 'data source registry ready.';
GO
