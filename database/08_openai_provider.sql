/* ============================================================================
 * 08_openai_provider.sql — เพิ่มผู้ให้บริการ AI รายที่สาม (OpenAI / ChatGPT)
 *
 * ขยาย CHECK constraint ของ ModelPricing.Provider ให้รับ 'OpenAI' แล้วใส่ราคาโมเดล
 *
 * ราคาจาก developers.openai.com/api/docs/pricing (ตรวจ 8 ก.ย. 2026)
 * USD ต่อ 1 ล้าน token — และ **ยิงทดสอบทีละตัวกับ key จริงแล้ว** ไม่ได้ดูจาก
 * GET /v1/models เพียงอย่างเดียว (บทเรียนจาก 07: รายการโมเดลบอกได้ไม่ครบว่าเรียกได้จริง)
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 08_openai_provider.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

/* ---------------------------------------------------------------- 1. ขยาย CHECK constraint */

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_ModelPricing_Provider')
BEGIN
    ALTER TABLE dbo.[ModelPricing] DROP CONSTRAINT [CK_ModelPricing_Provider];
    PRINT 'dropped old CK_ModelPricing_Provider';
END
GO

ALTER TABLE dbo.[ModelPricing]
    ADD CONSTRAINT [CK_ModelPricing_Provider]
    CHECK ([Provider] IN (N'Anthropic', N'Google', N'OpenAI'));
PRINT 'CK_ModelPricing_Provider now allows OpenAI';
GO

/* ---------------------------------------------------------------- 2. ราคาโมเดล OpenAI
 *
 * CacheWrite = 0 — OpenAI ทำ prompt caching อัตโนมัติและไม่คิดเงินตอนเขียน
 * เหมือน Gemini (ต่าง Anthropic ที่คิด 1.25x)
 *
 * ยืนยันด้วยการยิงจริง: prompt 10,027 token ยิงซ้ำครั้งที่สองได้ cached 10,024 token
 * โดย input_tokens ยังเป็น 10,027 เท่าเดิม -> cached ถูกนับ "รวมอยู่ใน" input_tokens
 * โค้ดจึงต้องหักออกก่อนคิดเงิน ไม่งั้นจ่ายราคาเต็มสำหรับ token ที่ควรถูกลง 10 เท่า
 *
 * ไม่ใส่ gpt-5.5-pro / gpt-5.4-pro (30/180 USD) เพราะแพงกว่าที่งานนี้ต้องใช้มาก
 * และหน้า pricing ระบุว่าไม่มี cached input ให้ */

DECLARE @rate DECIMAL(12,4) = (
    SELECT TOP 1 [UsdToThbRate] FROM dbo.[ModelPricing] ORDER BY [PricingID]);
SET @rate = ISNULL(@rate, 36.5000);

DECLARE @openai TABLE
(
    ModelName   NVARCHAR(100),
    InputUsd    DECIMAL(12,4),
    OutputUsd   DECIMAL(12,4),
    CacheRead   DECIMAL(12,4),
    IsActive    BIT,
    Notes       NVARCHAR(500)
);

INSERT INTO @openai (ModelName, InputUsd, OutputUsd, CacheRead, IsActive, Notes)
VALUES
    (N'gpt-5-nano',     0.05,  0.40, 0.0050, 0,
     N'ถูกที่สุดในระบบทุกผู้ให้บริการ แต่เป็นรุ่นปี 2025 — เปิดใช้เองได้จากหน้านี้'),

    (N'gpt-5.6-luna',   0.20,  1.20, 0.0200, 1,
     N'ChatGPT รุ่นประหยัด ใช้งานทั่วไป — ตอบเร็ว ไม่ใช้ reasoning token'),

    (N'gpt-5.4-mini',   0.75,  4.50, 0.0750, 0,
     N'รุ่นกลางของตระกูล 5.4 — ใช้ reasoning token ด้วย (คิดในอัตรา output)'),

    (N'gpt-5.6-terra',  2.00, 12.00, 0.2000, 1,
     N'ChatGPT รุ่นกลาง — สมดุลราคากับคุณภาพ'),

    (N'gpt-5.4',        2.50, 15.00, 0.2500, 0,
     N'รุ่นเรือธงของตระกูล 5.4 — ใช้ reasoning token'),

    (N'gpt-5.5',        5.00, 30.00, 0.5000, 1,
     N'ChatGPT รุ่นเรือธง — งานที่ต้องการคุณภาพสูง'),

    (N'gpt-6-astra',   10.00, 50.00, 1.0000, 0,
     N'รุ่นสูงสุด ราคาเท่า claude-fable-5-1 — เปิดใช้เมื่อจำเป็นจริง'),

    (N'gpt-4.1-mini',   0.40,  1.60, 0.1000, 0,
     N'รุ่นเก่าที่ไม่รับพารามิเตอร์ reasoning — ระบบตรวจเองแล้วยิงใหม่โดยไม่ใส่');

MERGE dbo.[ModelPricing] AS tgt
USING @openai AS src
    ON tgt.[ModelName] = src.ModelName
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([ModelName], [Provider], [InputUsdPerMTok], [OutputUsdPerMTok],
            [CacheWriteUsdPerMTok], [CacheReadUsdPerMTok],
            [UsdToThbRate], [IsActive], [Notes], [CreatedBy])
    VALUES (src.ModelName, N'OpenAI', src.InputUsd, src.OutputUsd,
            0, src.CacheRead,
            @rate, src.IsActive, src.Notes, N'seed-script');
GO

SELECT [Provider], [ModelName], [InputUsdPerMTok], [OutputUsdPerMTok],
       [CacheReadUsdPerMTok], [IsActive]
FROM dbo.[ModelPricing]
WHERE [IsActive] = 1
ORDER BY [InputUsdPerMTok];
GO

PRINT 'openai provider ready.';
GO
