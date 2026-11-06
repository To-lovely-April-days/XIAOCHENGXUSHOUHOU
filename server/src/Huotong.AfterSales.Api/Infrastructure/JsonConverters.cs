using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Huotong.AfterSales.Api.Infrastructure;

/// <summary>北京时间，格式 yyyy-MM-ddTHH:mm:ss（无时区）</summary>
public class BeijingDateTimeConverter : JsonConverter<DateTime>
{
    public const string Format = "yyyy-MM-ddTHH:mm:ss";
    private static readonly string[] Accept =
    {
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-dd"
    };

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (TryParse(s, out var dt)) return dt;
        throw new JsonException("时间格式应为 yyyy-MM-ddTHH:mm:ss");
    }

    public static bool TryParse(string? s, out DateTime dt)
    {
        dt = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var v = s.Trim();
        // 带时区的输入：转换为北京时间
        if (v.EndsWith("Z") || System.Text.RegularExpressions.Regex.IsMatch(v, @"[+-]\d{2}:\d{2}$"))
        {
            if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            {
                dt = DateTime.SpecifyKind(dto.ToOffset(TimeSpan.FromHours(8)).DateTime, DateTimeKind.Unspecified);
                return true;
            }
            return false;
        }
        if (DateTime.TryParseExact(v, Accept, CultureInfo.InvariantCulture, DateTimeStyles.None, out var r))
        {
            dt = DateTime.SpecifyKind(r, DateTimeKind.Unspecified);
            return true;
        }
        return false;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
}

public class DateOnlyConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        throw new JsonException("日期格式应为 yyyy-MM-dd");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

public static class JsonSetup
{
    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DictionaryKeyPolicy = null; // counts 的 key 原样输出（如 "Pending"）
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new BeijingDateTimeConverter());
        o.Converters.Add(new DateOnlyConverter());
        o.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    }
}
