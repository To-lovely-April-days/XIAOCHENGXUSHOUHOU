using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;

namespace Huotong.AfterSales.Tests;

public class GeoUtilTests
{
    [Fact]
    public void HaversineKnownDistance()
    {
        // 纬度 1 度约 111.2 km
        var d = GeoUtil.DistanceMeters(31.0, 120.0, 32.0, 120.0);
        Assert.InRange(d, 111_000, 111_400);
        Assert.Equal(0, GeoUtil.DistanceMeters(31.3, 120.72, 31.3, 120.72), 3);
    }

    [Fact]
    public void Threshold1000m()
    {
        Assert.False(GeoUtil.IsOutOfRange(999.9));
        Assert.False(GeoUtil.IsOutOfRange(1000));
        Assert.True(GeoUtil.IsOutOfRange(1000.1));
        var lat = GeoUtil.OffsetLatitude(32.06, 2400);
        var m = GeoUtil.DistanceMeters(32.06, 118.79, lat, 118.79);
        Assert.InRange(m, 2399, 2401);
        Assert.True(GeoUtil.IsOutOfRange(m));
        Assert.False(GeoUtil.IsOutOfRange(GeoUtil.DistanceMeters(31.30, 120.72, GeoUtil.OffsetLatitude(31.30, 86), 120.72)));
    }

    [Fact]
    public void DistanceText()
    {
        Assert.Equal("86 m", GeoUtil.DistanceText(86));
        Assert.Equal("2.4 km", GeoUtil.DistanceText(2400));
        Assert.Equal("1 km", GeoUtil.DistanceText(1000));
    }
}

public class CompletionValidatorTests
{
    private static CompletionInput Ok(int sig = 1) => new("接线端子氧化", "更换端子", RunStatus.Normal, 1, sig);

    [Fact]
    public void ValidPasses() => CompletionValidator.Validate(OrderStatus.InProgress, ServiceType.Onsite, false, Ok());

    [Fact]
    public void MustBeInProgress()
    {
        var ex = Assert.Throws<BizException>(() => CompletionValidator.Validate(OrderStatus.Accepted, ServiceType.Onsite, false, Ok()));
        Assert.Equal(40900, ex.Code);
    }

    [Fact]
    public void OnsiteRunningMustLeaveFirst()
    {
        var ex = Assert.Throws<BizException>(() => CompletionValidator.Validate(OrderStatus.InProgress, ServiceType.Onsite, true, Ok()));
        Assert.Equal(40900, ex.Code);
        Assert.Equal("请先离场打卡", ex.Message);
    }

    [Fact]
    public void RequiredFields()
    {
        Assert.Equal(40000, Assert.Throws<BizException>(() => CompletionValidator.Validate(OrderStatus.InProgress, ServiceType.Onsite, false,
            new CompletionInput(" ", "x", RunStatus.Normal, 1, 1))).Code);
        Assert.Equal(40000, Assert.Throws<BizException>(() => CompletionValidator.Validate(OrderStatus.InProgress, ServiceType.Onsite, false,
            new CompletionInput("x", null, RunStatus.Normal, 1, 1))).Code);
        Assert.Equal(40000, Assert.Throws<BizException>(() => CompletionValidator.Validate(OrderStatus.InProgress, ServiceType.Onsite, false,
            new CompletionInput("x", "y", null, 1, 1))).Code);
        Assert.Equal(40000, Assert.Throws<BizException>(() => CompletionValidator.Validate(OrderStatus.InProgress, ServiceType.Onsite, false,
            new CompletionInput("x", "y", RunStatus.Observe, 0, 1))).Code);
    }

    [Fact]
    public void SignatureRequiredOnlyForOnsite()
    {
        Assert.Throws<BizException>(() => CompletionValidator.Validate(OrderStatus.InProgress, ServiceType.Onsite, false, Ok(sig: 0)));
        CompletionValidator.Validate(OrderStatus.InProgress, ServiceType.Remote, false, Ok(sig: 0));
        CompletionValidator.Validate(OrderStatus.InProgress, ServiceType.Factory, true, Ok(sig: 0));
    }
}

public class AddressParserTests
{
    [Theory]
    [InlineData("苏州市工业园区星湖街 [示例地址] 3 号楼", "苏州", "苏州市工业园区")]
    [InlineData("江苏省苏州市工业园区星湖街", "苏州", "苏州市工业园区")]
    [InlineData("上海市杨浦区 [示例地址]", "上海", "上海市杨浦区")]
    [InlineData("无锡市新吴区 [示例地址]", "无锡", "无锡市新吴区")]
    [InlineData("浙江省杭州市钱塘区", "杭州", "杭州市钱塘区")]
    [InlineData("江苏省昆山市", "昆山", "昆山市")]
    [InlineData("浙江省", "浙江", "")]
    [InlineData("某工业园 3 号楼", "", "")]
    [InlineData("", "", "")]
    public void Parse(string address, string city, string region)
    {
        var (c, r) = AddressParser.Parse(address);
        Assert.Equal(city, c);
        Assert.Equal(region, r);
    }
}

public class OrderNoGeneratorTests
{
    private static readonly DateTime Day = new(2026, 11, 6, 10, 0, 0);

    [Fact]
    public void FirstOfDay() => Assert.Equal("SH-20261106-001", OrderNoGenerator.Next(Day, null));

    [Fact]
    public void Increments() => Assert.Equal("SH-20261106-015", OrderNoGenerator.Next(Day, "SH-20261106-014"));

    [Fact]
    public void OtherDayMaxIgnored() => Assert.Equal("SH-20261106-001", OrderNoGenerator.Next(Day, "SH-20261105-021"));

    [Fact]
    public void Beyond999() => Assert.Equal("SH-20261106-1000", OrderNoGenerator.Next(Day, "SH-20261106-999"));
}

public class LoadTextTests
{
    [Theory]
    [InlineData(0, 0, 0, null, "今日空闲 · 今日未出差")]
    [InlineData(0, 0, 1, null, "待验收 1 单 · 今日未出差")]
    [InlineData(1, 0, 0, "南京", "处理中 1 单 · 南京")]
    [InlineData(1, 1, 0, "苏州", "处理中 1 单、待启动 1 单 · 苏州")]
    [InlineData(1, 0, 0, null, "处理中 1 单")]
    [InlineData(0, 2, 0, null, "待启动 2 单 · 今日未出差")]
    public void Rules(int doing, int accepted, int submitted, string? city, string expected) =>
        Assert.Equal(expected, HomeService.LoadText(doing, accepted, submitted, city));
}

public class JsonFormatTests
{
    [Fact]
    public void DateTimeRoundTrip()
    {
        var opts = new System.Text.Json.JsonSerializerOptions();
        JsonSetup.Configure(opts);
        var json = System.Text.Json.JsonSerializer.Serialize(new { T = new DateTime(2026, 11, 6, 10, 5, 0), D = new DateOnly(2026, 11, 6), S = OrderStatus.Pending }, opts);
        Assert.Equal("{\"t\":\"2026-11-06T10:05:00\",\"d\":\"2026-11-06\",\"s\":\"Pending\"}", json);
        Assert.True(BeijingDateTimeConverter.TryParse("2026-11-06T02:05:00Z", out var dt));
        Assert.Equal(new DateTime(2026, 11, 6, 10, 5, 0), dt);
    }
}
