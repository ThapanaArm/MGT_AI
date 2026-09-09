/* ============================================================
   MGT_AI_Authen - Seed PolicyRules (question screening rules)
   Re-runnable (idempotent) - matched on RuleName

   Run with:
     sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P "<PASSWORD>" -C -f 65001 -i 02_seed_policy.sql

   Names and descriptions are in English (the UI language), but the detection
   patterns are BILINGUAL on purpose: employees may write in Thai or English, so
   a rule that only catches one language leaves the other wide open.
   Keyword patterns accept several terms separated by commas.
   ============================================================ */

USE [MGT_AI_Authen];
GO

DECLARE @seed TABLE
(
    RuleName      NVARCHAR(200),
    [Description] NVARCHAR(500),
    MatchType     NVARCHAR(20),
    Pattern       NVARCHAR(500),
    ActionType    NVARCHAR(20),
    Severity      NVARCHAR(20)
);

INSERT INTO @seed (RuleName, [Description], MatchType, Pattern, ActionType, Severity)
VALUES
    (N'Block passwords and system secrets',
     N'Never paste passwords, API keys or tokens into chat - they would be sent to the AI provider. Matches Thai and English wording.',
     N'Regex',
     N'(?i)(password\s*[:=]|passwd|api[_\s-]?key|secret[_\s-]?key|sk-ant-|bearer\s+ey|รหัสผ่าน\s*[:=]|รหัสผ่านคือ)',
     N'Block', N'High'),

    (N'Block Thai national ID numbers (13 digits)',
     N'Personal data under the Personal Data Protection Act (PDPA) must not leave the organisation.',
     N'Regex',
     N'\b\d{1}[- ]?\d{4}[- ]?\d{5}[- ]?\d{2}[- ]?\d{1}\b',
     N'Block', N'High'),

    (N'Block credit card numbers',
     N'13-16 digit card numbers must never be sent to an AI service.',
     N'Regex',
     N'\b(?:\d[ -]*?){13,16}\b',
     N'Block', N'High'),

    (N'Warn on salary and payroll data',
     N'Employee compensation is confidential - allowed through, but flagged for audit. Catches Thai and English terms.',
     N'Keyword',
     N'เงินเดือน, ค่าจ้าง, salary, payroll, compensation',
     N'Warn', N'Medium'),

    (N'Warn on product cost data',
     N'Product cost is commercially sensitive - allowed through, but flagged for audit.',
     N'Keyword',
     N'ต้นทุน, ราคาทุน, cost price, unit cost, margin',
     N'Warn', N'Medium'),

    (N'Audit questions mentioning customers',
     N'Flag questions that reference customer data so auditors can review them later.',
     N'Keyword',
     N'ลูกค้า, customer, client account',
     N'Audit', N'Low');

MERGE dbo.[PolicyRules] AS tgt
USING @seed AS src
    ON tgt.[RuleName] = src.RuleName
WHEN MATCHED THEN
    UPDATE SET tgt.[Description] = src.[Description],
               tgt.[MatchType]   = src.MatchType,
               tgt.[Pattern]     = src.Pattern,
               tgt.[ActionType]  = src.ActionType,
               tgt.[Severity]    = src.Severity,
               tgt.[UpdatedAt]   = SYSDATETIME(),
               tgt.[UpdatedBy]   = N'seed-script'
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([RuleName], [Description], [MatchType], [Pattern], [ActionType], [Severity], [IsActive], [CreatedBy])
    VALUES (src.RuleName, src.[Description], src.MatchType, src.Pattern, src.ActionType, src.Severity, 1, N'seed-script');
GO

/* Retire the original Thai-named rules so they are not enforced twice under two names.
   They are deactivated rather than deleted, so existing log rows still resolve their name. */
UPDATE dbo.[PolicyRules]
SET [IsActive] = 0,
    [UpdatedAt] = SYSDATETIME(),
    [UpdatedBy] = N'seed-script (superseded by the English-named rule)'
WHERE [CreatedBy] = N'seed-script'
  AND [IsActive] = 1
  AND [RuleName] IN (
      N'บล็อกรหัสผ่าน / ความลับระบบ',
      N'บล็อกเลขบัตรประชาชน (13 หลัก)',
      N'บล็อกเลขบัตรเครดิต',
      N'เตือนข้อมูลเงินเดือน / ค่าจ้าง',
      N'เตือนข้อมูลราคาต้นทุน',
      N'บันทึกคำถามเกี่ยวกับลูกค้า');
GO

SELECT [RuleID], [RuleName], [MatchType], [ActionType], [Severity], [IsActive]
FROM dbo.[PolicyRules]
ORDER BY [IsActive] DESC, [RuleID];
GO
