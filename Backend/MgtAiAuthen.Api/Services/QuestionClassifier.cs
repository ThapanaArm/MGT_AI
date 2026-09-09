namespace MgtAiAuthen.Api.Services;

/// <summary>
/// จัดหมวดคำถามของพนักงานอัตโนมัติ เพื่อให้ผู้ตรวจสอบกรอง log ได้ว่า
/// "ใครถามอะไร / ที่ไหน / อย่างไร" โดยไม่ต้องพิมพ์คำค้นเอง
///
/// ตรวจตามลำดับจากรูปแบบที่เฉพาะเจาะจงที่สุดไปกว้างที่สุด เพราะประโยคเดียว
/// อาจเข้าได้หลายหมวด (เช่น "ทำอย่างไร" มีทั้ง "ทำ" และ "อย่างไร")
/// </summary>
public static class QuestionClassifier
{
    public const string What = "WHAT";
    public const string Where = "WHERE";
    public const string How = "HOW";
    public const string Why = "WHY";
    public const string When = "WHEN";
    public const string Who = "WHO";
    public const string HowMuch = "HOWMUCH";
    public const string Other = "OTHER";

    /// <summary>ป้ายกำกับสำหรับแสดงบนหน้าจอและใน dropdown ตัวกรอง (UI เป็นภาษาอังกฤษ)</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [What] = "What",
        [Where] = "Where",
        [How] = "How",
        [Why] = "Why",
        [When] = "When",
        [Who] = "Who",
        [HowMuch] = "How much",
        [Other] = "Other",
    };

    /// <summary>ลำดับการตรวจมีความหมาย — อย่าเรียงใหม่โดยไม่ตรวจผลกับคำถามตัวอย่าง</summary>
    private static readonly (string Type, string[] Patterns)[] Rules =
    [
        (Why, ["ทำไม", "เพราะอะไร", "เพราะเหตุใด", "เหตุใด", "สาเหตุ", "why"]),
        (HowMuch, ["เท่าไหร่", "เท่าไร", "กี่บาท", "กี่ชิ้น", "กี่วัน", "กี่คน", "จำนวนเท่า", "how much", "how many"]),
        (How, ["อย่างไร", "ยังไง", "ยังไร", "วิธีการ", "วิธี", "ขั้นตอน", "ทำไง", "how to", "how do", "how can", "how"]),
        (Where, ["ที่ไหน", "ที่ใด", "แห่งใด", "อยู่ไหน", "จากไหน", "ตรงไหน", "where"]),
        (When, ["เมื่อไหร่", "เมื่อไร", "ตอนไหน", "วันไหน", "กี่โมง", "ช่วงไหน", "when"]),
        (Who, ["ใคร", "ผู้ใด", "ท่านใด", "who"]),
        (What, ["คืออะไร", "อะไร", "สิ่งใด", "หมายถึง", "นิยาม", "แปลว่า", "what", "which"]),
    ];

    /// <summary>คืนหมวดของข้อความ — คืน null ถ้าข้อความว่าง</summary>
    public static string? Classify(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        string text = content.ToLowerInvariant();

        foreach ((string type, string[] patterns) in Rules)
        {
            if (patterns.Any(p => text.Contains(p, StringComparison.Ordinal)))
            {
                return type;
            }
        }

        return Other;
    }

    public static string LabelOf(string? type)
        => type is not null && Labels.TryGetValue(type, out string? label) ? label : "Unspecified";
}
