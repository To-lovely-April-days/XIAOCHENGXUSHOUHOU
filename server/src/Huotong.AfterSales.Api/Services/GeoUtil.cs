namespace Huotong.AfterSales.Api.Services;

public static class GeoUtil
{
    public const int RangeMeters = 1000;
    private const double EarthRadius = 6371000d;

    /// <summary>Haversine 距离（米）</summary>
    public static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        double ToRad(double d) => d * Math.PI / 180d;
        var dLat = ToRad(lat2 - lat1);
        var dLng = ToRad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * EarthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>超过 1000m 视为距离异常（等于 1000 不算）</summary>
    public static bool IsOutOfRange(double meters) => meters > RangeMeters;

    /// <summary>距离文字：&lt;1000 显示 "86 m"，否则 "2.4 km"</summary>
    public static string DistanceText(int meters) =>
        meters < 1000 ? $"{meters} m" : $"{Math.Round(meters / 1000d, 1):0.#} km";

    /// <summary>向北偏移指定米数后的纬度（种子数据用）</summary>
    public static double OffsetLatitude(double lat, double meters) => lat + meters / (EarthRadius * Math.PI / 180d);
}
