/* ============================================================================
 * 06_gemini_provider.sql — เพิ่มผู้ให้บริการ AI รายที่สอง (Google Gemini)
 *
 * เดิมระบบผูกกับ Anthropic รายเดียว ตาราง ModelPricing จึงไม่มีคอลัมน์บอกว่า
 * โมเดลแต่ละแถวเป็นของใคร สคริปต์นี้เพิ่ม Provider แล้ว backfill แถวเดิมเป็น
 * 'Anthropic' และเพิ่มราคาของโมเดล Gemini
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 06_gemini_provider.sql
 *
 * หมายเหตุ (บทเรียนจาก 03/04): ห้ามใช้ PERSISTED computed column และ filtered index
 * บนตารางที่ DBA ต้องแก้ด้วย sqlcmd เพราะจะบังคับให้ทุกคำสั่ง DML ต้องรันด้วย
 * QUOTED_IDENTIFIER ON ไม่งั้นได้ error Msg 1934 โดยไม่มีสาเหตุที่เดาได้
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

/* ---------------------------------------------------------------- 1. คอลัมน์ Provider */

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'dbo.ModelPricing') AND [name] = N'Provider')
BEGIN
    /* เพิ่มแบบ NULL ก่อน เพื่อ backfill แถวเดิมได้ก่อนบังคับ NOT NULL */
    ALTER TABLE dbo.[ModelPricing] ADD [Provider] NVARCHAR(20) NULL;
    PRINT 'added ModelPricing.Provider';
END
ELSE
BEGIN
    PRINT 'ModelPricing.Provider already exists — skipped';
END
GO

/* แถวที่มีอยู่ก่อนสคริปต์นี้เป็นของ Anthropic ทั้งหมด */
UPDATE dbo.[ModelPricing]
SET [Provider] = N'Anthropic'
WHERE [Provider] IS NULL;
GO

/* ค่าตั้งต้นสำหรับแถวที่ INSERT โดยไม่ระบุ provider */
IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE [name] = N'DF_ModelPricing_Provider')
BEGIN
    ALTER TABLE dbo.[ModelPricing]
        ADD CONSTRAINT [DF_ModelPricing_Provider] DEFAULT (N'Anthropic') FOR [Provider];
    PRINT 'added DF_ModelPricing_Provider';
END
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'dbo.ModelPricing')
      AND [name] = N'Provider' AND [is_nullable] = 1)
BEGIN
    ALTER TABLE dbo.[ModelPricing] ALTER COLUMN [Provider] NVARCHAR(20) NOT NULL;
    PRINT 'ModelPricing.Provider is now NOT NULL';
END
GO

/* ชื่อผู้ให้บริการที่โค้ดรู้จักมีสองค่านี้เท่านั้น — สะกดผิดต้องล้มที่ฐานข้อมูล
 * ไม่ใช่ปล่อยให้ router หาผู้ให้บริการไม่เจอตอน runtime */
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_ModelPricing_Provider')
BEGIN
    ALTER TABLE dbo.[ModelPricing]
        ADD CONSTRAINT [CK_ModelPricing_Provider]
        CHECK ([Provider] IN (N'Anthropic', N'Google'));
    PRINT 'added CK_ModelPricing_Provider';
END
GO

/* index ปกติ ไม่ใช่ filtered (ดูหมายเหตุหัวไฟล์) */
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ModelPricing_Provider'
      AND [object_id] = OBJECT_ID(N'dbo.ModelPricing'))
BEGIN
    CREATE INDEX [IX_ModelPricing_Provider]
        ON dbo.[ModelPricing] ([Provider], [IsActive]);
    PRINT 'added IX_ModelPricing_Provider';
END
GO

/* ---------------------------------------------------------------- 2. ราคาโมเดล Gemini */

/* ราคาจาก ai.google.dev/gemini-api/docs/pricing (ตรวจเมื่อ 2026-09-07) หน่วยเป็น
 * USD ต่อ 1 ล้าน token ของ paid tier
 *
 * CacheWrite = 0 โดยเจตนา — Gemini ไม่คิดเงินตอน "เขียน" cache สำหรับ implicit
 * caching (ต่างจาก Anthropic ที่คิด 1.25x) ระบบนี้ใช้ implicit caching เท่านั้น
 * จึงมีแต่ฝั่งอ่านที่มีค่าใช้จ่าย
 *
 * ข้อควรระวังที่ตารางนี้เก็บไม่ได้: gemini-2.5-pro และ gemini-3.1-pro-preview
 * คิดราคา *สองขั้น* ตามความยาว prompt (เกิน 200k token ราคาขึ้นเท่าตัว)
 * ตารางนี้เก็บราคาเดียว จึงบันทึกไว้ที่ราคาขั้นต่ำ (<= 200k) — prompt ที่ยาวเกินนั้น
 * จะถูกคิดเงินต่ำกว่าจริง ดูหัวข้อ 6.18 ใน README */

DECLARE @rate DECIMAL(12,4) = (
    SELECT TOP 1 [UsdToThbRate] FROM dbo.[ModelPricing] ORDER BY [PricingID]);
SET @rate = ISNULL(@rate, 36.5000);

DECLARE @gemini TABLE
(
    ModelName   NVARCHAR(100),
    InputUsd    DECIMAL(12,4),
    OutputUsd   DECIMAL(12,4),
    CacheRead   DECIMAL(12,4),
    IsActive    BIT,
    Notes       NVARCHAR(500)
);

INSERT INTO @gemini (ModelName, InputUsd, OutputUsd, CacheRead, IsActive, Notes)
VALUES
    (N'gemini-2.5-flash-lite',   0.10,  0.40, 0.0100, 0,
     N'Gemini ที่ถูกที่สุด — เปิดใช้เองได้จากหน้านี้'),

    (N'gemini-2.5-flash',        0.30,  2.50, 0.0300, 1,
     N'Gemini รุ่นประหยัด ใช้งานทั่วไป'),

    (N'gemini-3.5-flash-lite',   0.30,  2.50, 0.0300, 0,
     N'รุ่น lite ของตระกูล 3.5 — เปิดใช้เองได้จากหน้านี้'),

    (N'gemini-3.8-flash',        0.75,  3.75, 0.0750, 1,
     N'Gemini Flash รุ่นใหม่สุด — ราคานี้เป็นราคาโปรโมชันถึง 31 ธ.ค. 2026 หลังจากนั้นขึ้นเท่าตัว (1.50/7.50)'),

    (N'gemini-3.7-flash',        0.75,  3.75, 0.0750, 0,
     N'ราคาเท่ารุ่น 3.8 และเป็นราคาโปรโมชันถึง 31 ธ.ค. 2026 เช่นกัน'),

    (N'gemini-2.5-pro',          1.25, 10.00, 0.1250, 1,
     N'Gemini Pro — ราคานี้คือขั้น prompt <= 200k token เกินกว่านั้นราคาขึ้นเท่าตัว'),

    (N'gemini-3.1-pro-preview',  2.00, 12.00, 0.2000, 0,
     N'Preview — โมเดล preview อาจถูกถอนได้ตลอด และคิดราคาสองขั้นที่ 200k token');

MERGE dbo.[ModelPricing] AS tgt
USING @gemini AS src
    ON tgt.[ModelName] = src.ModelName
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([ModelName], [Provider], [InputUsdPerMTok], [OutputUsdPerMTok],
            [CacheWriteUsdPerMTok], [CacheReadUsdPerMTok],
            [UsdToThbRate], [IsActive], [Notes], [CreatedBy])
    VALUES (src.ModelName, N'Google', src.InputUsd, src.OutputUsd,
            0, src.CacheRead,
            @rate, src.IsActive, src.Notes, N'seed-script');
GO

SELECT [PricingID], [Provider], [ModelName], [InputUsdPerMTok], [OutputUsdPerMTok],
       [CacheWriteUsdPerMTok], [CacheReadUsdPerMTok], [UsdToThbRate], [IsActive]
FROM dbo.[ModelPricing]
ORDER BY [Provider], [InputUsdPerMTok];
GO

PRINT 'gemini provider ready.';
GO
