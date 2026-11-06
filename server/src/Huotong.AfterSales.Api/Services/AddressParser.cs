using System.Text.RegularExpressions;

namespace Huotong.AfterSales.Api.Services;

public static class AddressParser
{
    private static readonly Regex Re = new(
        @"^(?<prov>[一-龥]{2,7}?(省|自治区|特别行政区))?(?<city>[一-龥]{2,9}?(市|自治州|地区|盟))?(?<dist>[一-龥]{1,8}?(区|县|旗|市))?",
        RegexOptions.Compiled);

    /// <summary>从地址解析 city（去掉“市”的短名）与 region（市+区县）</summary>
    public static (string City, string Region) Parse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return ("", "");
        var s = address.Trim().Replace(" ", "");
        var m = Re.Match(s);
        if (!m.Success) return ("", "");
        var prov = m.Groups["prov"].Value;
        var city = m.Groups["city"].Value;
        var dist = m.Groups["dist"].Value;
        if (city.Length == 0)
        {
            // 没有市：给省份短名
            return (ShortProvince(prov), "");
        }
        var shortCity = city.EndsWith("市") ? city[..^1] : city;
        return (shortCity, city + dist);
    }

    private static string ShortProvince(string prov)
    {
        if (prov.Length == 0) return "";
        foreach (var suffix in new[] { "特别行政区", "壮族自治区", "回族自治区", "维吾尔自治区", "自治区", "省" })
            if (prov.EndsWith(suffix)) return prov[..^suffix.Length];
        return prov;
    }
}
