/* ============================================================
   MGT_AI_Authen - Chat / Code mode per message
   Re-runnable (idempotent)

   Run with:
     sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P "<PASSWORD>" -C -f 65001 -i 05_chat_mode.sql

   Mode is stored per MESSAGE, not per conversation, so a user can switch between
   Chat and Code mid-thread and the audit trail still shows which mode produced
   each answer. Existing rows are backfilled as 'Chat'.
   ============================================================ */

USE [MGT_AI_Authen];
GO

IF COL_LENGTH('dbo.ChatMessages', 'ChatMode') IS NULL
BEGIN
    ALTER TABLE dbo.[ChatMessages] ADD [ChatMode] NVARCHAR(20) NULL;
END
GO

/* Backfill history so filtering by mode does not silently hide older messages */
UPDATE dbo.[ChatMessages]
SET [ChatMode] = N'Chat'
WHERE [ChatMode] IS NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ChatMessages_ChatMode'
)
    ALTER TABLE dbo.[ChatMessages]
        ADD CONSTRAINT CK_ChatMessages_ChatMode CHECK ([ChatMode] IN (N'Chat', N'Code'));
GO

/* Plain (not filtered) index — a filtered index would force QUOTED_IDENTIFIER ON
   for every DML statement on this table, which sqlcmd leaves OFF. */
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_ChatMessages_ChatMode' AND object_id = OBJECT_ID('dbo.ChatMessages')
)
    CREATE INDEX IX_ChatMessages_ChatMode ON dbo.[ChatMessages]([ChatMode]);
GO

SELECT [ChatMode], COUNT(*) AS Messages
FROM dbo.[ChatMessages]
GROUP BY [ChatMode];
GO

PRINT 'chat mode ready.';
GO
