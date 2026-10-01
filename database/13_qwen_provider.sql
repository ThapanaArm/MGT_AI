/* ============================================================================
 * 13_qwen_provider.sql — เพิ่มผู้ให้บริการ AI รายที่สี่ (Alibaba Cloud Model Studio / Qwen)
 *
 * ขยาย CHECK constraint ของ ModelPricing.Provider ให้รับ 'Qwen' แล้วใส่ราคาโมเดล
 *
 * ราคาจากหน้าเอกสาร/บล็อกราคาของ Alibaba Model Studio (ตรวจ ต.ค. 2026) — ยังไม่ได้
 * ยิงทดสอบทีละรุ่นกับ key จริง (ต่างจากสคริปต์ 08 ที่ยิงทดสอบแล้ว) ดังนั้น **ต้องตรวจสอบ
 * ชื่อโมเดลและราคาอีกครั้งกับหน้า console จริงก่อนใช้ในระบบ production** — ชื่อโมเดลของ
 * Alibaba มีทั้งแบบ alias (ชี้ไปรุ่นล่าสุดเสมอ เช่น qwen-plus, qwen-turbo) และแบบ snapshot
 * วันที่ระบุรุ่นตายตัว ถ้ายิงแล้วได้ 404 ให้ตรวจชื่อที่ถูกต้องจากหน้า Model Studio ของบัญชีนั้น
 *
 * CacheRead ตั้งเป็น 0 ทุกแถว — ยังไม่ยืนยันอัตราส่วนลด context cache ของ Alibaba กับระบบนี้
 * (ต่างจาก OpenAI/Gemini ที่ยืนยันตัวเลขจากการยิงจริงแล้ว) ตั้งเป็น 0 ไว้ก่อนเพื่อไม่ให้ประเมิน
 * ต้นทุนต่ำกว่าความเป็นจริง — ถ้ามีข้อมูลยืนยันแล้วค่อยแก้ตัวเลขนี้
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 13_qwen_provider.sql
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
    CHECK ([Provider] IN (N'Anthropic', N'Google', N'OpenAI', N'Qwen'));
PRINT 'CK_ModelPricing_Provider now allows Qwen';
GO

/* ---------------------------------------------------------------- 2. ราคาโมเดล Qwen */

DECLARE @rate DECIMAL(12,4) = (
    SELECT TOP 1 [UsdToThbRate] FROM dbo.[ModelPricing] ORDER BY [PricingID]);
SET @rate = ISNULL(@rate, 36.5000);

DECLARE @qwen TABLE
(
    ModelName   NVARCHAR(100),
    InputUsd    DECIMAL(12,4),
    OutputUsd   DECIMAL(12,4),
    IsActive    BIT,
    Notes       NVARCHAR(500)
);

INSERT INTO @qwen (ModelName, InputUsd, OutputUsd, IsActive, Notes)
VALUES
    (N'qwen-turbo',     0.05,  0.20, 1,
     N'ถูกและเร็วที่สุดของ Qwen — เหมาะงานง่าย ๆ ปริมาณมาก'),

    (N'qwen-plus',      0.40,  1.20, 1,
     N'สมดุลราคา/คุณภาพ — ค่าตั้งต้นของผู้ให้บริการนี้ (Qwen:Model ใน appsettings)'),

    (N'qwen3.5-flash',  0.10,  0.40, 0,
     N'รุ่นประหยัดของตระกูล 3.5 — เปิดใช้เองได้จากหน้านี้'),

    (N'qwen3.5-plus',   0.40,  2.40, 0,
     N'รุ่นกลางของตระกูล 3.5 — context window ใหญ่กว่า qwen-plus'),

    (N'qwen3.8-flash',  0.14,  0.42, 0,
     N'รุ่นประหยัดรุ่นใหม่ล่าสุด (ส.ค. 2026) — เปิดใช้เองได้จากหน้านี้'),

    (N'qwen3.7-max',    1.25,  3.75, 0,
     N'รุ่นเรือธงรุ่นก่อน — ราคาถูกกว่า 3.8-max'),

    (N'qwen3.8-max',    2.00,  6.00, 1,
     N'เรือธงล่าสุดของ Qwen (ส.ค. 2026) — ใช้เมื่อต้องการคุณภาพสูงสุด');

MERGE dbo.[ModelPricing] AS tgt
USING @qwen AS src
    ON tgt.[ModelName] = src.ModelName
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([ModelName], [Provider], [InputUsdPerMTok], [OutputUsdPerMTok],
            [CacheWriteUsdPerMTok], [CacheReadUsdPerMTok],
            [UsdToThbRate], [IsActive], [Notes], [CreatedBy])
    VALUES (src.ModelName, N'Qwen', src.InputUsd, src.OutputUsd,
            0, 0,
            @rate, src.IsActive, src.Notes, N'seed-script');
GO

SELECT [Provider], [ModelName], [InputUsdPerMTok], [OutputUsdPerMTok],
       [CacheReadUsdPerMTok], [IsActive]
FROM dbo.[ModelPricing]
WHERE [IsActive] = 1
ORDER BY [InputUsdPerMTok];
GO

PRINT 'qwen provider ready.';
GO
