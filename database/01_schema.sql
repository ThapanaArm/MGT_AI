/* ============================================================
   MGT_AI_Authen - Database schema
   ระบบ Chat AI (Claude) พร้อม Login / Audit Log / Policy Filter

   รันด้วย:
     sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P "<PASSWORD>" -C -i 01_schema.sql
   ============================================================ */

IF DB_ID('MGT_AI_Authen') IS NULL
    CREATE DATABASE [MGT_AI_Authen];
GO

USE [MGT_AI_Authen];
GO

/* ---------- 1. Users : ผู้ใช้งานระบบ ---------- */
IF OBJECT_ID('dbo.Users', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.[Users]
    (
        [UserID]             INT             IDENTITY(1,1) NOT NULL,
        [Username]           NVARCHAR(100)   NOT NULL,
        [Email]              NVARCHAR(200)   NOT NULL,
        [FullName]           NVARCHAR(200)   NOT NULL,
        [Department]         NVARCHAR(100)   NULL,
        /* รูปแบบที่เก็บ: PBKDF2$<iterations>$<salt_b64>$<hash_b64> */
        [PasswordHash]       NVARCHAR(400)   NOT NULL,
        /* Admin = ดู log ทุกคน + จัดการ user/policy | Auditor = ดู log ได้เท่านั้น | User = chat เท่านั้น */
        [UserRole]           NVARCHAR(20)    NOT NULL CONSTRAINT DF_Users_UserRole DEFAULT ('User'),
        [IsActive]           BIT             NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1),
        [MustChangePassword] BIT             NOT NULL CONSTRAINT DF_Users_MustChangePassword DEFAULT (0),
        [FailedLoginCount]   INT             NOT NULL CONSTRAINT DF_Users_FailedLoginCount DEFAULT (0),
        [LockoutUntil]       DATETIME2(0)    NULL,
        [LastLoginAt]        DATETIME2(0)    NULL,
        [CreatedAt]          DATETIME2(0)    NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT (SYSDATETIME()),
        [UpdatedAt]          DATETIME2(0)    NULL,
        CONSTRAINT PK_Users PRIMARY KEY CLUSTERED ([UserID]),
        CONSTRAINT UQ_Users_Username UNIQUE ([Username]),
        CONSTRAINT CK_Users_UserRole CHECK ([UserRole] IN ('Admin', 'Auditor', 'User'))
    );
END
GO

/* ---------- 2. RefreshTokens : ใช้ต่ออายุ JWT ---------- */
IF OBJECT_ID('dbo.RefreshTokens', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.[RefreshTokens]
    (
        [TokenID]       BIGINT          IDENTITY(1,1) NOT NULL,
        [UserID]        INT             NOT NULL,
        /* เก็บเฉพาะ SHA-256 ของ token ไม่เก็บค่าจริง */
        [TokenHash]     NVARCHAR(200)   NOT NULL,
        [ExpiresAt]     DATETIME2(0)    NOT NULL,
        [CreatedAt]     DATETIME2(0)    NOT NULL CONSTRAINT DF_RefreshTokens_CreatedAt DEFAULT (SYSDATETIME()),
        [CreatedByIp]   NVARCHAR(64)    NULL,
        [RevokedAt]     DATETIME2(0)    NULL,
        [RevokedReason] NVARCHAR(200)   NULL,
        CONSTRAINT PK_RefreshTokens PRIMARY KEY CLUSTERED ([TokenID]),
        CONSTRAINT FK_RefreshTokens_Users FOREIGN KEY ([UserID])
            REFERENCES dbo.[Users]([UserID]) ON DELETE CASCADE
    );
    CREATE INDEX IX_RefreshTokens_TokenHash ON dbo.[RefreshTokens]([TokenHash]);
    CREATE INDEX IX_RefreshTokens_UserID    ON dbo.[RefreshTokens]([UserID]);
END
GO

/* ---------- 3. PolicyRules : เงื่อนไขคัดกรองคำถามก่อนส่งให้ Claude ---------- */
IF OBJECT_ID('dbo.PolicyRules', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.[PolicyRules]
    (
        [RuleID]       INT            IDENTITY(1,1) NOT NULL,
        [RuleName]     NVARCHAR(200)  NOT NULL,
        [Description]  NVARCHAR(500)  NULL,
        /* Keyword = หาคำตรงตัวแบบไม่สนตัวพิมพ์เล็กใหญ่ | Regex = .NET regular expression */
        [MatchType]    NVARCHAR(20)   NOT NULL CONSTRAINT DF_PolicyRules_MatchType DEFAULT ('Keyword'),
        [Pattern]      NVARCHAR(500)  NOT NULL,
        /* Block = ไม่ส่งให้ AI | Warn = ส่งแต่เตือนผู้ใช้ + ติดธง | Audit = ส่งตามปกติแต่ติดธงไว้ */
        [ActionType]   NVARCHAR(20)   NOT NULL CONSTRAINT DF_PolicyRules_ActionType DEFAULT ('Warn'),
        [Severity]     NVARCHAR(20)   NOT NULL CONSTRAINT DF_PolicyRules_Severity DEFAULT ('Medium'),
        [IsActive]     BIT            NOT NULL CONSTRAINT DF_PolicyRules_IsActive DEFAULT (1),
        [CreatedAt]    DATETIME2(0)   NOT NULL CONSTRAINT DF_PolicyRules_CreatedAt DEFAULT (SYSDATETIME()),
        [CreatedBy]    NVARCHAR(100)  NULL,
        [UpdatedAt]    DATETIME2(0)   NULL,
        [UpdatedBy]    NVARCHAR(100)  NULL,
        CONSTRAINT PK_PolicyRules PRIMARY KEY CLUSTERED ([RuleID]),
        CONSTRAINT CK_PolicyRules_MatchType  CHECK ([MatchType]  IN ('Keyword', 'Regex')),
        CONSTRAINT CK_PolicyRules_ActionType CHECK ([ActionType] IN ('Block', 'Warn', 'Audit')),
        CONSTRAINT CK_PolicyRules_Severity   CHECK ([Severity]   IN ('Low', 'Medium', 'High'))
    );
END
GO

/* ---------- 4. ChatSessions : 1 แถว = 1 ห้องสนทนา ---------- */
IF OBJECT_ID('dbo.ChatSessions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.[ChatSessions]
    (
        [SessionID]    UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ChatSessions_SessionID DEFAULT (NEWID()),
        [UserID]       INT              NOT NULL,
        [Title]        NVARCHAR(300)    NOT NULL CONSTRAINT DF_ChatSessions_Title DEFAULT (N'การสนทนาใหม่'),
        [MessageCount] INT              NOT NULL CONSTRAINT DF_ChatSessions_MessageCount DEFAULT (0),
        [IsDeleted]    BIT              NOT NULL CONSTRAINT DF_ChatSessions_IsDeleted DEFAULT (0),
        [CreatedAt]    DATETIME2(0)     NOT NULL CONSTRAINT DF_ChatSessions_CreatedAt DEFAULT (SYSDATETIME()),
        [UpdatedAt]    DATETIME2(0)     NOT NULL CONSTRAINT DF_ChatSessions_UpdatedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ChatSessions PRIMARY KEY CLUSTERED ([SessionID]),
        CONSTRAINT FK_ChatSessions_Users FOREIGN KEY ([UserID])
            REFERENCES dbo.[Users]([UserID]) ON DELETE CASCADE
    );
    CREATE INDEX IX_ChatSessions_UserID_UpdatedAt ON dbo.[ChatSessions]([UserID], [UpdatedAt] DESC);
END
GO

/* ---------- 5. ChatMessages : ทุกข้อความที่พนักงานถาม / AI ตอบ ---------- */
IF OBJECT_ID('dbo.ChatMessages', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.[ChatMessages]
    (
        [MessageID]      BIGINT           IDENTITY(1,1) NOT NULL,
        [SessionID]      UNIQUEIDENTIFIER NOT NULL,
        /* เก็บ UserID ซ้ำไว้ที่นี่ เพื่อค้น log ตามพนักงานได้เร็วโดยไม่ต้อง join */
        [UserID]         INT              NOT NULL,
        [MessageRole]    NVARCHAR(20)     NOT NULL,
        [Content]        NVARCHAR(MAX)    NOT NULL,
        /* ประเภทคำถามที่จัดหมวดอัตโนมัติ: WHAT / WHERE / HOW / WHY / WHEN / WHO / HOWMUCH / OTHER */
        [QuestionType]   NVARCHAR(20)     NULL,
        [ModelName]      NVARCHAR(100)    NULL,
        [InputTokens]    INT              NULL,
        [OutputTokens]   INT              NULL,
        [LatencyMs]      INT              NULL,
        [IsBlocked]      BIT              NOT NULL CONSTRAINT DF_ChatMessages_IsBlocked DEFAULT (0),
        [PolicyFlag]     NVARCHAR(20)     NULL,     /* Block / Warn / Audit */
        [PolicyRuleID]   INT              NULL,
        [PolicyRuleName] NVARCHAR(200)    NULL,
        [ClientIp]       NVARCHAR(64)     NULL,
        [UserAgent]      NVARCHAR(400)    NULL,
        [CreatedAt]      DATETIME2(0)     NOT NULL CONSTRAINT DF_ChatMessages_CreatedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ChatMessages PRIMARY KEY CLUSTERED ([MessageID]),
        CONSTRAINT FK_ChatMessages_Sessions FOREIGN KEY ([SessionID])
            REFERENCES dbo.[ChatSessions]([SessionID]) ON DELETE CASCADE,
        CONSTRAINT FK_ChatMessages_Users FOREIGN KEY ([UserID])
            REFERENCES dbo.[Users]([UserID]),
        CONSTRAINT CK_ChatMessages_MessageRole CHECK ([MessageRole] IN ('user', 'assistant', 'system'))
    );
    CREATE INDEX IX_ChatMessages_CreatedAt        ON dbo.[ChatMessages]([CreatedAt] DESC);
    CREATE INDEX IX_ChatMessages_UserID_CreatedAt ON dbo.[ChatMessages]([UserID], [CreatedAt] DESC);
    CREATE INDEX IX_ChatMessages_SessionID        ON dbo.[ChatMessages]([SessionID], [MessageID]);
    CREATE INDEX IX_ChatMessages_QuestionType     ON dbo.[ChatMessages]([QuestionType]);
END
GO

/* ---------- 6. AuditLogs : เหตุการณ์ระดับระบบ (login / logout / ค้น log / แก้ policy) ---------- */
IF OBJECT_ID('dbo.AuditLogs', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.[AuditLogs]
    (
        [AuditID]    BIGINT         IDENTITY(1,1) NOT NULL,
        [UserID]     INT            NULL,
        [Username]   NVARCHAR(100)  NULL,
        [Category]   NVARCHAR(30)   NOT NULL,   /* AUTH / CHAT / ADMIN / POLICY */
        [Action]     NVARCHAR(60)   NOT NULL,   /* LOGIN_SUCCESS / LOGIN_FAILED / LOGOUT / CHAT_SENT / ... */
        [Detail]     NVARCHAR(MAX)  NULL,
        [IsSuccess]  BIT            NOT NULL CONSTRAINT DF_AuditLogs_IsSuccess DEFAULT (1),
        [ClientIp]   NVARCHAR(64)   NULL,
        [UserAgent]  NVARCHAR(400)  NULL,
        [CreatedAt]  DATETIME2(0)   NOT NULL CONSTRAINT DF_AuditLogs_CreatedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_AuditLogs PRIMARY KEY CLUSTERED ([AuditID])
    );
    CREATE INDEX IX_AuditLogs_CreatedAt ON dbo.[AuditLogs]([CreatedAt] DESC);
    CREATE INDEX IX_AuditLogs_UserID    ON dbo.[AuditLogs]([UserID], [CreatedAt] DESC);
    CREATE INDEX IX_AuditLogs_Category  ON dbo.[AuditLogs]([Category], [Action]);
END
GO

PRINT 'MGT_AI_Authen schema ready.';
GO
