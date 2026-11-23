using System.Globalization;
using System.Text;

namespace Huotong.AfterSales.Api.Services;

/// <summary>订阅消息的语义槽（v1.4）：配置里 WeChat:Subscribe:Fields 的值只能取这些名字</summary>
public enum SubscribeSlot
{
    /// <summary>事项，如「新工单指派给你」</summary>
    Title,
    /// <summary>工单号</summary>
    OrderNo,
    /// <summary>客户名称</summary>
    Customer,
    /// <summary>说明（一句话）</summary>
    Content,
    /// <summary>事件时间</summary>
    Time,
    /// <summary>操作人姓名</summary>
    Operator,
    /// <summary>工单状态中文</summary>
    Status,
    /// <summary>设备汇总文字（DeviceText）</summary>
    Device
}

/// <summary>一条订阅消息各语义槽的取值。任何槽位都不得放价格、价格建议、受限附件名</summary>
public record SubscribeSlots(string Title, string OrderNo, string Customer, string Content, DateTime Time, string Operator, string Status, string Device);

/// <summary>
/// 订阅消息字段的清洗 / 截断（纯函数，v1.4）。微信按「字段键的前缀」限定取值格式，不合规整条消息会被拒（47003），
/// 所以发送前一律按前缀清洗；空值给合规占位，因为微信不接受空字段。
/// </summary>
public static class SubscribeFields
{
    public const string TimeFormat = "yyyy'年'MM'月'dd'日' HH':'mm";
    public const string DateFormat = "yyyy'年'MM'月'dd'日'";

    /// <summary>字段键的类型前缀：thing1 → thing，character_string12 → character_string（小写、去掉末尾序号）</summary>
    public static string Kind(string key) => (key ?? "").Trim().ToLowerInvariant().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    /// <summary>槽名解析（不区分大小写）；不认识的槽名返回 null</summary>
    public static SubscribeSlot? ParseSlot(string? name)
    {
        var s = (name ?? "").Trim();
        if (s.Length == 0 || !char.IsLetter(s[0])) return null;
        return Enum.TryParse<SubscribeSlot>(s, true, out var slot) && Enum.IsDefined(slot) ? slot : null;
    }

    /// <summary>
    /// 按字段键的前缀清洗一段文字：
    /// thing ≤20 字符；character_string ≤32 且只留数字 / 字母 / 符号；phrase ≤5 个汉字且只留汉字；
    /// name 含汉字时只留汉字与字母、≤10 字，否则只留字母与符号、≤20 字；number 只留数字、≤32 位；其余前缀 ≤20 字符。
    /// 所有类型都先压成一行并去掉表情、零宽字符等（见 Flat）。
    /// 清洗后为空 → 合规占位（「-」「无」「0」）。time / date 字段请用 <see cref="FormatTime"/>。
    /// </summary>
    public static string Clean(string key, string? text)
    {
        var s = Flat(text);
        switch (Kind(key))
        {
            case "character_string":
                return OrDefault(Take(Keep(s, c => c is > ' ' and <= '~'), 32), "-");
            case "phrase":
                return OrDefault(Take(Keep(s, IsHan), 5), "无");
            case "name":
                return s.Any(IsHan)
                    ? OrDefault(Take(Keep(s, c => IsHan(c) || IsAsciiLetter(c)), 10), "无")
                    : OrDefault(Take(Keep(s, c => IsAsciiLetter(c) || c is ' ' or '.' or '-' or '·').Trim(' ', '.', '-', '·'), 20), "无");
            case "number":
                return OrDefault(Take(Keep(s, c => c is >= '0' and <= '9'), 32), "0");
            default: // thing 与其它前缀：20 字符以内，超出截断并以「…」结尾
                return OrDefault(s.Length <= 20 ? s : Take(s, 19).TrimEnd() + "…", "-");
        }
    }

    /// <summary>time → 「yyyy年MM月dd日 HH:mm」，date → 「yyyy年MM月dd日」</summary>
    public static string FormatTime(string key, DateTime time) =>
        time.ToString(Kind(key) == "date" ? DateFormat : TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// 按配置的字段映射生成微信 data 的「字段键 → 值」（键统一为小写）。
    /// time / date 字段无论映射到哪个槽，都填事件时间（这两类字段只接受时间）；映射到不认识的槽名时填占位。
    /// </summary>
    public static Dictionary<string, string> Build(IEnumerable<KeyValuePair<string, string>> fields, SubscribeSlots slots)
    {
        var data = new Dictionary<string, string>();
        foreach (var (rawKey, slotName) in fields)
        {
            var key = (rawKey ?? "").Trim().ToLowerInvariant();
            if (key.Length == 0) continue;
            var kind = Kind(key);
            if (kind is "time" or "date")
            {
                data[key] = FormatTime(key, slots.Time);
                continue;
            }
            data[key] = Clean(key, ParseSlot(slotName) switch
            {
                SubscribeSlot.Title => slots.Title,
                SubscribeSlot.OrderNo => slots.OrderNo,
                SubscribeSlot.Customer => slots.Customer,
                SubscribeSlot.Content => slots.Content,
                SubscribeSlot.Operator => slots.Operator,
                SubscribeSlot.Status => slots.Status,
                SubscribeSlot.Device => slots.Device,
                // 事件时间放进非时间字段：按字段类型给一个清洗后仍可读的写法
                SubscribeSlot.Time => slots.Time.ToString(kind switch
                {
                    "character_string" => "yyyy-MM-dd'T'HH':'mm",
                    "number" => "yyyyMMddHHmm",
                    _ => TimeFormat
                }, CultureInfo.InvariantCulture),
                _ => null
            });
        }
        return data;
    }

    // ---------- 内部 ----------

    private static bool IsHan(char c) => c is >= '一' and <= '鿿';

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static string OrDefault(string s, string placeholder) => s.Length == 0 ? placeholder : s;

    /// <summary>
    /// 压成一行：换行 / 制表等空白一律当作一个空格，去掉首尾空白，并去掉微信字段可能不认的字符（见 <see cref="IsJunk"/>）。
    /// 只剩这类字符的值会变成空，由调用方给占位——否则「看起来是空的」值（如只有零宽空格）会让整条消息被拒。
    /// </summary>
    private static string Flat(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var r in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r)) { space = sb.Length > 0; continue; }
            if (IsJunk(r)) continue;
            if (space) sb.Append(' ');
            space = false;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// 表情、零宽 / 方向控制符、变体选择符、私用区、落单的代理项等：微信文档只说「汉字、数字、字母或符号」，
    /// 这些字符是否被接受没有保证，保守起见一律去掉（丢一个表情不影响提醒，整条被拒才是问题）。
    /// 非基本平面只保留文字（扩展区汉字，人名 / 地名里会有）。
    /// </summary>
    private static bool IsJunk(Rune r)
    {
        switch (Rune.GetUnicodeCategory(r))
        {
            case UnicodeCategory.Control:
            case UnicodeCategory.Format:
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.Surrogate:
            case UnicodeCategory.OtherNotAssigned:
                return true;
        }
        var v = r.Value;
        if (v == 0xFFFD || v == 0x20E3 || v is >= 0xFE00 and <= 0xFE0F) return true; // 替换符（落单代理项）、键帽、变体选择符
        if (v > 0xFFFF) return !Rune.IsLetter(r);
        // 基本平面里的表情：⌚⌛⌨⏏、⏩…⏺，以及杂项符号 / 装饰符号 / 杂项箭头三个区。其它符号（℃、→、⌀、①…）保留
        return v is 0x231A or 0x231B or 0x2328 or 0x23CF or >= 0x23E9 and <= 0x23FA
            or >= 0x2600 and <= 0x27BF or >= 0x2B00 and <= 0x2BFF;
    }

    private static string Keep(string s, Func<char, bool> ok)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (ok(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>取前 max 个字符（按 UTF-16 长度计），不把表情等代理对 / 组合字符切成两半</summary>
    private static string Take(string s, int max)
    {
        if (s.Length <= max) return s;
        var sb = new StringBuilder(max);
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var el = e.GetTextElement();
            if (sb.Length + el.Length > max) break;
            sb.Append(el);
        }
        return sb.ToString();
    }
}
