using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.4 订阅消息：字段清洗 / 截断的纯函数、槽名解析、配置检查</summary>
public sealed class V14SubscribeFieldTests
{
    private static readonly DateTime T = new(2026, 11, 6, 13, 42, 10);

    private static SubscribeSlots Slots(string title = "新工单指派给你", string content = "赵主管把工单指派给你，请及时处理", string device = "高压反应釜 HTR-250") =>
        new(title, "SH-20261106-001", "苏州某某新材料有限公司", content, T, "赵主管", "待启动", device);

    [Theory]
    [InlineData("thing1", "thing")]
    [InlineData("character_string12", "character_string")]
    [InlineData(" Time3 ", "time")]
    [InlineData("PHRASE4", "phrase")]
    [InlineData("name", "name")]
    public void 字段键的类型前缀(string key, string kind) => Assert.Equal(kind, SubscribeFields.Kind(key));

    [Fact]
    public void Thing_二十字以内原样_超出截断加省略号_空白压成一行()
    {
        Assert.Equal("新工单指派给你", SubscribeFields.Clean("thing1", "新工单指派给你"));
        var twenty = new string('字', 20);
        Assert.Equal(twenty, SubscribeFields.Clean("thing1", twenty));
        var cut = SubscribeFields.Clean("thing1", new string('字', 21));
        Assert.Equal(20, cut.Length);
        Assert.Equal(new string('字', 19) + "…", cut);
        Assert.Equal("第一行 第二行", SubscribeFields.Clean("thing2", "  第一行\r\n\t 第二行 \n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void Thing_空值给占位(string? v) => Assert.Equal("-", SubscribeFields.Clean("thing1", v));

    [Fact]
    public void Thing_表情与零宽字符去掉_常用符号保留_只剩这类字符时给占位()
    {
        Assert.Equal("苏州某某公司", SubscribeFields.Clean("thing1", "苏州😀某某公司👍🏻"));
        Assert.Equal("A B", SubscribeFields.Clean("thing1", "A 😀 B"));
        Assert.Equal("已完成", SubscribeFields.Clean("thing1", "已完成\u2705\uFE0F"));
        Assert.Equal("中国", SubscribeFields.Clean("thing1", "🇨🇳中\u200B国\u200D"));
        // 常用的温度、箭头、括号等符号不动
        Assert.Equal("温度180℃→200℃（偏高）", SubscribeFields.Clean("thing1", "温度180℃→200℃（偏高）"));
        Assert.Equal("管径⌀50mm ±0.1 ①", SubscribeFields.Clean("thing1", "管径⌀50mm ±0.1 ①"));
        Assert.Equal("定时到点", SubscribeFields.Clean("thing1", "⏰定时到点⭐"));
        // 只有表情 / 零宽字符 / 落单代理项：清洗后为空 → 占位（微信不接受空字段）
        Assert.Equal("-", SubscribeFields.Clean("thing1", "😀👍"));
        Assert.Equal("-", SubscribeFields.Clean("thing1", "\u200B\u200D\uFEFF"));
        Assert.Equal("-", SubscribeFields.Clean("thing1", "\ud83d"));
        Assert.Equal("无", SubscribeFields.Clean("phrase1", "😀"));
    }

    [Fact]
    public void Thing_表情去掉之后再算长度()
    {
        // 18 个汉字 + 2 个表情：表情去掉后只有 18 个字，不需要截断
        Assert.Equal(new string('字', 18), SubscribeFields.Clean("thing1", new string('字', 18) + "😀😀"));
    }

    [Fact]
    public void Thing_扩展区汉字保留_截断不把它切成两半()
    {
        Assert.Equal("𠮷田", SubscribeFields.Clean("thing1", "𠮷田"));
        // 18 个汉字 + 2 个扩展区汉字（各占 2 个 UTF-16 单元）= 22：只能留 18 个汉字 + 「…」
        var v = SubscribeFields.Clean("thing1", new string('字', 18) + "𠮷𠮷");
        Assert.Equal(new string('字', 18) + "…", v);
        Assert.DoesNotContain('\ud842', v);
    }

    [Fact]
    public void CharacterString_只留数字字母符号_最多三十二位_空了给占位()
    {
        Assert.Equal("SH-20261106-001", SubscribeFields.Clean("character_string2", "SH-20261106-001"));
        Assert.Equal("SH-001", SubscribeFields.Clean("character_string2", "工单 SH-001 号"));
        Assert.Equal(32, SubscribeFields.Clean("character_string2", new string('A', 40)).Length);
        Assert.Equal("-", SubscribeFields.Clean("character_string2", "全是汉字"));
        Assert.Equal("-", SubscribeFields.Clean("character_string2", null));
    }

    [Fact]
    public void Phrase_只留汉字_最多五个_不合规降级()
    {
        Assert.Equal("处理中", SubscribeFields.Clean("phrase4", "处理中"));
        Assert.Equal("暂停搁置", SubscribeFields.Clean("phrase4", "暂停搁置"));
        Assert.Equal("新工单指派", SubscribeFields.Clean("phrase4", "新工单指派给你"));
        Assert.Equal("完工被驳回", SubscribeFields.Clean("phrase4", "完工被驳回，请重新处理"));
        Assert.Equal("待验收", SubscribeFields.Clean("phrase4", "待验收 OK 123"));
        Assert.Equal("无", SubscribeFields.Clean("phrase4", "abc 123"));
        Assert.Equal("无", SubscribeFields.Clean("phrase4", ""));
    }

    [Fact]
    public void Name_含汉字最多十个字_纯字母最多二十个()
    {
        Assert.Equal("赵主管", SubscribeFields.Clean("name5", "赵主管"));
        Assert.Equal("王工号", SubscribeFields.Clean("name5", "王工2号"));
        Assert.Equal("王Tom", SubscribeFields.Clean("name5", "王 Tom"));
        Assert.Equal(10, SubscribeFields.Clean("name5", new string('张', 14)).Length);
        Assert.Equal("Tom Hanks", SubscribeFields.Clean("name5", "Tom Hanks 3"));
        Assert.Equal(20, SubscribeFields.Clean("name5", new string('a', 30)).Length);
        Assert.Equal("无", SubscribeFields.Clean("name5", "12345"));
        Assert.Equal("无", SubscribeFields.Clean("name5", null));
    }

    [Fact]
    public void Number_只留数字()
    {
        Assert.Equal("20261106001", SubscribeFields.Clean("number3", "SH-20261106-001"));
        Assert.Equal(32, SubscribeFields.Clean("number3", new string('9', 40)).Length);
        Assert.Equal("0", SubscribeFields.Clean("number3", "没有数字"));
    }

    [Fact]
    public void 其它前缀按二十字符截断()
    {
        Assert.Equal(20, SubscribeFields.Clean("letter1", new string('x', 30)).Length);
        Assert.Equal("-", SubscribeFields.Clean("symbol1", ""));
    }

    [Fact]
    public void 时间与日期格式()
    {
        Assert.Equal("2026年11月06日 13:42", SubscribeFields.FormatTime("time3", T));
        Assert.Equal("2026年11月06日", SubscribeFields.FormatTime("date3", T));
        Assert.Equal("2026年01月02日 03:04", SubscribeFields.FormatTime("time1", new DateTime(2026, 1, 2, 3, 4, 59)));
    }

    [Fact]
    public void 槽名解析不区分大小写_不认识的返回空()
    {
        Assert.Equal(SubscribeSlot.Title, SubscribeFields.ParseSlot("title"));
        Assert.Equal(SubscribeSlot.OrderNo, SubscribeFields.ParseSlot(" OrderNo "));
        Assert.Equal(SubscribeSlot.Device, SubscribeFields.ParseSlot("DEVICE"));
        Assert.Null(SubscribeFields.ParseSlot("Price"));
        Assert.Null(SubscribeFields.ParseSlot("3"));
        Assert.Null(SubscribeFields.ParseSlot(""));
        Assert.Null(SubscribeFields.ParseSlot(null));
    }

    [Fact]
    public void 语义槽集合固定为八个_没有价格()
    {
        Assert.Equal(new[] { "Title", "OrderNo", "Customer", "Content", "Time", "Operator", "Status", "Device" }, Enum.GetNames<SubscribeSlot>());
    }

    [Fact]
    public void Build_按映射取值并清洗_键统一小写()
    {
        var data = SubscribeFields.Build(new Dictionary<string, string>
        {
            ["Thing1"] = "Title", ["character_string2"] = "orderno", ["thing3"] = "Customer", ["thing4"] = "Content",
            ["time5"] = "Time", ["name6"] = "Operator", ["phrase7"] = "Status", ["thing8"] = "Device"
        }, Slots());
        Assert.Equal(new[] { "character_string2", "name6", "phrase7", "thing1", "thing3", "thing4", "thing8", "time5" }, data.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("新工单指派给你", data["thing1"]);
        Assert.Equal("SH-20261106-001", data["character_string2"]);
        Assert.Equal("苏州某某新材料有限公司", data["thing3"]);
        Assert.Equal("赵主管把工单指派给你，请及时处理", data["thing4"]);
        Assert.Equal("2026年11月06日 13:42", data["time5"]);
        Assert.Equal("赵主管", data["name6"]);
        Assert.Equal("待启动", data["phrase7"]);
        Assert.Equal("高压反应釜 HTR-250", data["thing8"]);
    }

    [Fact]
    public void Build_空槽给占位_不认识的槽名给占位_时间字段一律填事件时间()
    {
        var data = SubscribeFields.Build(new Dictionary<string, string>
        {
            ["thing1"] = "Device", ["thing2"] = "NoSuchSlot", ["time3"] = "Title", ["date4"] = "Time", ["phrase5"] = "Device", ["number6"] = "Device"
        }, Slots(device: ""));
        Assert.Equal("-", data["thing1"]);
        Assert.Equal("-", data["thing2"]);
        Assert.Equal("2026年11月06日 13:42", data["time3"]);
        Assert.Equal("2026年11月06日", data["date4"]);
        Assert.Equal("无", data["phrase5"]);
        Assert.Equal("0", data["number6"]);
        Assert.All(data.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    [Fact]
    public void Build_事件时间放进非时间字段也合规()
    {
        var data = SubscribeFields.Build(new Dictionary<string, string>
        {
            ["thing1"] = "Time", ["character_string2"] = "Time", ["number3"] = "Time"
        }, Slots());
        Assert.Equal("2026年11月06日 13:42", data["thing1"]);
        Assert.Equal("2026-11-06T13:42", data["character_string2"]);
        Assert.Equal("202611061342", data["number3"]);
    }

    [Fact]
    public void Build_超长内容按字段类型截断()
    {
        var data = SubscribeFields.Build(new Dictionary<string, string> { ["thing1"] = "Content", ["phrase2"] = "Title" },
            Slots(title: "完工被驳回，请重新处理", content: "李工想把工单转交给你：电气部分我搞不定，需要更熟的同事来处理"));
        Assert.Equal(20, data["thing1"].Length);
        Assert.EndsWith("…", data["thing1"]);
        Assert.StartsWith("李工想把工单转交给你：", data["thing1"]);
        Assert.Equal("完工被驳回", data["phrase2"]);
    }

    // ---------- 配置 ----------

    [Fact]
    public void 已启用_模板非空且至少一个字段()
    {
        Assert.False(new SubscribeOptions().Enabled);
        Assert.False(new SubscribeOptions { TemplateId = "TPL" }.Enabled);
        Assert.False(new SubscribeOptions { TemplateId = "  ", Fields = { ["thing1"] = "Title" } }.Enabled);
        Assert.False(new SubscribeOptions { Fields = { ["thing1"] = "Title" } }.Enabled);
        Assert.True(new SubscribeOptions { TemplateId = "TPL", Fields = { ["thing1"] = "Title" } }.Enabled);
    }

    [Fact]
    public void Fields配置成null_按没有映射处理_不抛异常()
    {
        var o = new SubscribeOptions { TemplateId = "TPL", Fields = null! };
        Assert.False(o.Enabled);
        Assert.Empty(o.Fields);
        Assert.Single(SubscribeService.ConfigProblems(o));
    }

    [Fact]
    public void 默认配置_试用版_指向工单详情()
    {
        var o = new SubscribeOptions();
        Assert.Equal("trial", o.MiniProgramState);
        Assert.Equal("pages/order-detail/index?id={id}", o.Page);
        Assert.Empty(o.Fields);
        Assert.Equal("", o.TemplateId);
    }

    [Fact]
    public void 配置检查_未启用不提示_写错逐条提示()
    {
        Assert.Empty(SubscribeService.ConfigProblems(new SubscribeOptions()));
        Assert.Empty(SubscribeService.ConfigProblems(new SubscribeOptions { TemplateId = "TPL", MiniProgramState = "formal", Fields = { ["thing1"] = "Title" } }));

        var p = SubscribeService.ConfigProblems(new SubscribeOptions { TemplateId = "TPL", MiniProgramState = "release", Fields = { ["thing1"] = "Price" } });
        Assert.Equal(2, p.Count);
        Assert.Contains(p, x => x.Contains("thing1") && x.Contains("Price"));
        Assert.Contains(p, x => x.Contains("release"));

        Assert.Single(SubscribeService.ConfigProblems(new SubscribeOptions { TemplateId = "TPL" }));
        Assert.Single(SubscribeService.ConfigProblems(new SubscribeOptions { Fields = { ["thing1"] = "Title" } }));
    }
}
