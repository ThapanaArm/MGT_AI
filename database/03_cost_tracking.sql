/* ============================================================
   MGT_AI_Authen - บันทึก token และค่าใช้จ่ายต่อการแชท
   รันซ้ำได้ (idempotent)

   รันด้วย:
     sqlcmd -S "<SERVER>\SQLEXPRESS" -U <USER> -P "<PASSWORD>" -C -f 65001 -i 03_cost_tracking.sql

   หลักการ: คำนวณค่าใช้จ่าย ณ เวลาที่บันทึกข้อความ แล้ว "แช่แข็ง" ค่าไว้ในแถวนั้น
   เมื่อภายหลังมีการปรับราคาหรืออัตราแลกเปลี่ยน ค่าใช้จ่ายย้อนหลังจะไม่เปลี่ยนตาม
   ทำให้รายงานค่าใช้จ่ายของเดือนที่ปิดไปแล้วนิ่ง ตรวจสอบย้อนหลังได้
   ============================================================ */

USE [MGT_AI_Authen];
GO

/* จำเป็นสำหรับสร้าง PERSISTED computed column — sqlcmd ตั้งค่านี้เป็น OFF โดยปริยาย
   ถ้าไม่ SET จะได้ error Msg 1934 'QUOTED_IDENTIFIER' (หรือรัน sqlcmd ด้วยสวิตช์ -I แทน) */
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- 1. ตารางราคาต่อโมเดล ---------- */
IF OBJECT_ID('dbo.ModelPricing', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.[ModelPricing]
    (
        [PricingID]        INT            IDENTITY(1,1) NOT NULL,
        [ModelName]        NVARCHAR(100)  NOT NULL,

        /* ราคาต่อ 1 ล้าน token (หน่วย USD) ตามราคาประกาศของ Anthropic */
        [InputUsdPerMTok]      DECIMAL(12,4) NOT NULL,
        [OutputUsdPerMTok]     DECIMAL(12,4) NOT NULL,
        /* เขียน cache = 1.25x ของ input, อ่าน cache = 0.1x ของ input */
        [CacheWriteUsdPerMTok] DECIMAL(12,4) NOT NULL,
        [CacheReadUsdPerMTok]  DECIMAL(12,4) NOT NULL,

        /* อัตราแลกเปลี่ยนที่ใช้แปลงเป็นบาท — เก็บไว้เพื่อให้ตรวจย้อนหลังได้ว่าใช้เรตเท่าไร */
        [UsdToThbRate]     DECIMAL(12,4)  NOT NULL CONSTRAINT DF_ModelPricing_Rate DEFAULT (36.5),

        [EffectiveFrom]    DATETIME2(0)   NOT NULL CONSTRAINT DF_ModelPricing_EffectiveFrom DEFAULT (SYSDATETIME()),
        [IsActive]         BIT            NOT NULL CONSTRAINT DF_ModelPricing_IsActive DEFAULT (1),
        [Notes]            NVARCHAR(500)  NULL,
        [CreatedAt]        DATETIME2(0)   NOT NULL CONSTRAINT DF_ModelPricing_CreatedAt DEFAULT (SYSDATETIME()),
        [CreatedBy]        NVARCHAR(100)  NULL,
        [UpdatedAt]        DATETIME2(0)   NULL,
        [UpdatedBy]        NVARCHAR(100)  NULL,
        CONSTRAINT PK_ModelPricing PRIMARY KEY CLUSTERED ([PricingID]),
        CONSTRAINT UQ_ModelPricing_Model UNIQUE ([ModelName])
    );
END
GO

/* ---------- 2. คอลัมน์ token/ค่าใช้จ่ายใน ChatMessages ---------- */

/* token ที่เกี่ยวกับ prompt cache — มีราคาต่างจาก input ปกติ */
IF COL_LENGTH('dbo.ChatMessages', 'CacheWriteTokens') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [CacheWriteTokens] INT NULL;
GO

IF COL_LENGTH('dbo.ChatMessages', 'CacheReadTokens') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [CacheReadTokens] INT NULL;
GO

/* รวม token ทุกประเภทของข้อความนี้ — ใช้คอลัมน์คำนวณเพื่อไม่ให้ค่ารวมหลุดจากส่วนย่อย
 *
 * ตั้งใจไม่ใช้ PERSISTED: ถ้า persist แล้ว ทุกคำสั่ง INSERT/UPDATE/DELETE บนตารางนี้
 * จะต้องรันด้วย QUOTED_IDENTIFIER ON ไม่งั้นได้ error Msg 1934 — ซึ่ง sqlcmd ตั้งเป็น OFF
 * โดยปริยาย ทำให้ DBA รันคำสั่งแก้ข้อมูลไม่ได้โดยไม่ทราบสาเหตุ
 * (แอปผ่าน SqlClient ตั้ง ON อยู่แล้ว จึงไม่กระทบ แต่ไม่คุ้มกับกับดักฝั่งปฏิบัติการ)
 * การคำนวณเป็นแค่การบวกเลข 4 ตัว จึงไม่มีผลด้านประสิทธิภาพที่วัดได้
 */
IF COL_LENGTH('dbo.ChatMessages', 'TotalTokens') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [TotalTokens] AS (
        ISNULL([InputTokens], 0) + ISNULL([OutputTokens], 0)
        + ISNULL([CacheWriteTokens], 0) + ISNULL([CacheReadTokens], 0)
    );
GO

/* ถ้าเคยรันเวอร์ชันก่อนหน้าที่สร้างเป็น PERSISTED ให้เปลี่ยนเป็นแบบไม่ persist */
IF EXISTS (
    SELECT 1 FROM sys.computed_columns
    WHERE object_id = OBJECT_ID('dbo.ChatMessages')
      AND name = 'TotalTokens'
      AND is_persisted = 1
)
BEGIN
    ALTER TABLE dbo.[ChatMessages] DROP COLUMN [TotalTokens];

    ALTER TABLE dbo.[ChatMessages] ADD [TotalTokens] AS (
        ISNULL([InputTokens], 0) + ISNULL([OutputTokens], 0)
        + ISNULL([CacheWriteTokens], 0) + ISNULL([CacheReadTokens], 0)
    );

    PRINT 'เปลี่ยน TotalTokens จาก PERSISTED เป็นคอลัมน์คำนวณแบบปกติแล้ว';
END
GO

/* ค่าใช้จ่ายแยกตามประเภท (USD) — DECIMAL(18,8) รองรับค่าเล็กระดับ 0.00000001 */
IF COL_LENGTH('dbo.ChatMessages', 'InputCostUsd') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [InputCostUsd] DECIMAL(18,8) NULL;
GO

IF COL_LENGTH('dbo.ChatMessages', 'OutputCostUsd') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [OutputCostUsd] DECIMAL(18,8) NULL;
GO

IF COL_LENGTH('dbo.ChatMessages', 'CacheCostUsd') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [CacheCostUsd] DECIMAL(18,8) NULL;
GO

IF COL_LENGTH('dbo.ChatMessages', 'TotalCostUsd') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [TotalCostUsd] DECIMAL(18,8) NULL;
GO

IF COL_LENGTH('dbo.ChatMessages', 'TotalCostThb') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [TotalCostThb] DECIMAL(18,6) NULL;
GO

/* เก็บเรตและ PricingID ที่ใช้ตอนคำนวณ เพื่อให้ตรวจสอบย้อนหลังได้ว่าคิดจากอะไร */
IF COL_LENGTH('dbo.ChatMessages', 'UsdToThbRate') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [UsdToThbRate] DECIMAL(12,4) NULL;
GO

IF COL_LENGTH('dbo.ChatMessages', 'PricingID') IS NULL
    ALTER TABLE dbo.[ChatMessages] ADD [PricingID] INT NULL;
GO

/* index ช่วยรายงานค่าใช้จ่ายรายคน/รายวัน */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ChatMessages_Cost' AND object_id = OBJECT_ID('dbo.ChatMessages'))
    CREATE INDEX IX_ChatMessages_Cost
        ON dbo.[ChatMessages]([CreatedAt] DESC)
        INCLUDE ([UserID], [TotalCostUsd], [TotalCostThb], [InputTokens], [OutputTokens]);
GO

/* ---------- 3. ราคาตั้งต้น ---------- */
/* อ้างอิงราคาประกาศของ Anthropic (USD ต่อ 1 ล้าน token)
   cache write = input x 1.25, cache read = input x 0.1
   *** ตรวจสอบราคาล่าสุดที่ https://www.anthropic.com/pricing ก่อนใช้คิดเงินจริง *** */
DECLARE @rate DECIMAL(12,4) = 36.5;   /* อัตราแลกเปลี่ยนตั้งต้น - ปรับได้ในหน้าจอผู้ดูแลระบบ */

DECLARE @prices TABLE
(
    ModelName        NVARCHAR(100),
    InputUsd         DECIMAL(12,4),
    OutputUsd        DECIMAL(12,4),
    Notes            NVARCHAR(500)
);

INSERT INTO @prices (ModelName, InputUsd, OutputUsd, Notes)
VALUES
    (N'claude-sonnet-5', 2.00,  10.00, N'โมเดลตั้งต้นของระบบ'),
    (N'claude-opus-5',   5.00,  25.00, N'งานที่ต้องการความสามารถสูงสุด'),
    (N'claude-opus-4-8', 5.00,  25.00, N'ใช้เป็นโมเดลสำรอง'),
    (N'claude-haiku-4-5', 1.00,  5.00, N'งานง่าย ๆ เน้นเร็ว'),
    (N'claude-fable-5-1', 10.00, 50.00, N'โมเดลระดับสูงสุด');

MERGE dbo.[ModelPricing] AS tgt
USING @prices AS src
    ON tgt.[ModelName] = src.ModelName
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([ModelName], [InputUsdPerMTok], [OutputUsdPerMTok],
            [CacheWriteUsdPerMTok], [CacheReadUsdPerMTok],
            [UsdToThbRate], [IsActive], [Notes], [CreatedBy])
    VALUES (src.ModelName, src.InputUsd, src.OutputUsd,
            src.InputUsd * 1.25, src.InputUsd * 0.1,
            @rate, 1, src.Notes, N'seed-script');
GO

SELECT [PricingID], [ModelName], [InputUsdPerMTok], [OutputUsdPerMTok],
       [CacheWriteUsdPerMTok], [CacheReadUsdPerMTok], [UsdToThbRate], [IsActive]
FROM dbo.[ModelPricing]
ORDER BY [PricingID];
GO

PRINT 'cost tracking ready.';
GO
