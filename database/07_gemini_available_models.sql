/* ============================================================================
 * 07_gemini_available_models.sql — แก้รายชื่อโมเดล Gemini ให้ตรงกับที่เรียกได้จริง
 *
 * 06_gemini_provider.sql ใส่ราคาไว้ตามหน้า pricing ของ Google แต่พอเอา API key จริง
 * มายิงทีละตัวพบว่า **ตระกูล 2.5 เรียกไม่ได้เลยสำหรับบัญชีที่เปิดใหม่**
 *
 *   404 "This model models/gemini-2.5-flash is no longer available to new users.
 *        Please update your code to use models/gemini-3.6-flash"
 *
 * บทเรียน: endpoint GET /v1beta/models **แสดงโมเดลที่บัญชีนั้นเรียกไม่ได้ด้วย**
 * จะรู้ว่าเรียกได้จริงต้องยิง generateContent ทดสอบ ไม่ใช่ดูจากรายการ
 *
 * ผลการยิงทดสอบทั้ง 13 ตัว (7 ก.ย. 2026):
 *   เรียกได้  3.8-flash, 3.7-flash, 3.6-flash, 3.5-flash, 3.5-flash-lite,
 *             3.1-flash-lite, 3.1-pro-preview
 *   เรียกไม่ได้  2.5-flash, 2.5-flash-lite, 2.5-pro
 *
 * รันด้วย:
 *   sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P <PASSWORD> -f 65001 -i 07_gemini_available_models.sql
 * ============================================================================ */

USE [MGT_AI_Authen];
GO

/* ---------------------------------------------------------------- 1. ปิดตระกูล 2.5
 *
 * ปิดใช้งาน ไม่ลบ — ข้อความเก่าอ้าง PricingID อยู่ และบัญชีที่เปิดไว้นานแล้ว
 * อาจยังเรียกโมเดลเหล่านี้ได้ ถ้าย้ายไปใช้ key อื่นก็เปิดกลับได้จากหน้า Model pricing */

UPDATE dbo.[ModelPricing]
SET [IsActive] = 0,
    [Notes] = N'Google ถอนจากบัญชีที่เปิดใหม่แล้ว (404 no longer available to new users) — ยิงทดสอบ 7 ก.ย. 2026',
    [UpdatedAt] = SYSDATETIME(),
    [UpdatedBy] = N'seed-script'
WHERE [Provider] = N'Google'
  AND [ModelName] IN (N'gemini-2.5-flash', N'gemini-2.5-flash-lite', N'gemini-2.5-pro');
GO

/* ---------------------------------------------------------------- 2. เพิ่มรุ่นที่เรียกได้
 *
 * ราคาจาก ai.google.dev/gemini-api/docs/pricing (7 ก.ย. 2026) USD ต่อ 1 ล้าน token
 * CacheWrite = 0 เพราะ Google ไม่คิดเงินตอนเขียน implicit cache
 *
 * ไม่ใส่ gemini-3.1-flash-lite ทั้งที่เรียกได้ เพราะหน้า pricing ไม่ได้ประกาศราคาไว้
 * — ใส่โมเดลที่ไม่รู้ราคาจะถูกบันทึกค่าใช้จ่ายเป็น 0 เงียบ ๆ ซึ่งเคยเป็นบั๊กมาแล้ว */

DECLARE @rate DECIMAL(12,4) = (
    SELECT TOP 1 [UsdToThbRate] FROM dbo.[ModelPricing] ORDER BY [PricingID]);
SET @rate = ISNULL(@rate, 36.5000);

DECLARE @add TABLE
(
    ModelName   NVARCHAR(100),
    InputUsd    DECIMAL(12,4),
    OutputUsd   DECIMAL(12,4),
    CacheRead   DECIMAL(12,4),
    IsActive    BIT,
    Notes       NVARCHAR(500)
);

INSERT INTO @add (ModelName, InputUsd, OutputUsd, CacheRead, IsActive, Notes)
VALUES
    (N'gemini-3.6-flash', 0.75, 3.75, 0.0750, 0,
     N'รุ่นที่ Google แนะนำแทน 2.5-flash — ราคาเท่า 3.8 จึงปิดไว้ ใช้ 3.8 ดีกว่า'),

    (N'gemini-3.5-flash', 1.50, 9.00, 0.1500, 0,
     N'Legacy และแพงกว่า 3.8-flash ทั้งที่เก่ากว่า จึงปิดไว้');

MERGE dbo.[ModelPricing] AS tgt
USING @add AS src
    ON tgt.[ModelName] = src.ModelName
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([ModelName], [Provider], [InputUsdPerMTok], [OutputUsdPerMTok],
            [CacheWriteUsdPerMTok], [CacheReadUsdPerMTok],
            [UsdToThbRate], [IsActive], [Notes], [CreatedBy])
    VALUES (src.ModelName, N'Google', src.InputUsd, src.OutputUsd,
            0, src.CacheRead,
            @rate, src.IsActive, src.Notes, N'seed-script');
GO

/* ---------------------------------------------------------------- 3. เปิดชุดที่ใช้จริง
 *
 * เลือกสามตัวให้ครอบคลุม ถูก / กลาง / เก่ง เหมือนฝั่ง Claude */

UPDATE dbo.[ModelPricing]
SET [IsActive] = 1,
    [Notes] = N'Gemini ที่ถูกที่สุดที่เรียกได้ — ไม่มี thinking token',
    [UpdatedAt] = SYSDATETIME(), [UpdatedBy] = N'seed-script'
WHERE [ModelName] = N'gemini-3.5-flash-lite';

UPDATE dbo.[ModelPricing]
SET [IsActive] = 1,
    [Notes] = N'Gemini Flash รุ่นใหม่สุด — ราคาโปรโมชันถึง 31 ธ.ค. 2026 หลังจากนั้นขึ้นเท่าตัว (1.50/7.50)',
    [UpdatedAt] = SYSDATETIME(), [UpdatedBy] = N'seed-script'
WHERE [ModelName] = N'gemini-3.8-flash';

UPDATE dbo.[ModelPricing]
SET [IsActive] = 1,
    [Notes] = N'Gemini Pro ตัวเดียวที่บัญชีนี้เรียกได้ — เป็น preview (ถูกถอนได้ตลอด) และคิดราคาสองขั้นที่ prompt 200k token',
    [UpdatedAt] = SYSDATETIME(), [UpdatedBy] = N'seed-script'
WHERE [ModelName] = N'gemini-3.1-pro-preview';
GO

SELECT [Provider], [ModelName], [InputUsdPerMTok], [OutputUsdPerMTok],
       [CacheReadUsdPerMTok], [IsActive]
FROM dbo.[ModelPricing]
ORDER BY [Provider], [IsActive] DESC, [InputUsdPerMTok];
GO

PRINT 'gemini model list matches what the API actually serves.';
GO
