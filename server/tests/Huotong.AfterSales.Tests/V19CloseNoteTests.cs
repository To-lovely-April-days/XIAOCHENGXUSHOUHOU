using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.9 确认闭环时可以写备注</summary>
public sealed class V19CloseNoteTests : V11TestBase
{
    private async Task<int> ReadyToClose()
    {
        // 远程服务不用打卡：认领 → 开始 → 提交完工 → 定价，就到了可以闭环的状态
        var wang = U("王工");
        var id = await NewAccepted("闭环备注客户", service: ServiceType.Remote);
        await Orders.Start(id, wang);
        await Orders.Complete(id, Done(wang, signature: false), wang);
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        return id;
    }

    [Fact]
    public async Task NoteIsWrittenIntoTheCloseLog()
    {
        var id = await ReadyToClose();
        var d = await Orders.Close(id, Sup, "  资料齐全，客户已签字  ");
        Assert.Equal(OrderStatus.Closed, d.Status);
        Assert.Equal("赵主管 确认闭环 → 已闭环（资料齐全，客户已签字）", d.Logs[^1].Text);
    }

    [Fact]
    public async Task NoteIsOptional()
    {
        var id = await ReadyToClose();
        var d = await Orders.Close(id, Sup, "   ");
        Assert.Equal("赵主管 确认闭环 → 已闭环", d.Logs[^1].Text);
    }

    [Fact]
    public async Task NoteLongerThan200IsRejectedAndOrderStaysOpen()
    {
        var id = await ReadyToClose();
        await Fails(40000, "备注不能超过 200 个字", () => Orders.Close(id, Sup, new string('长', 201)));
        Assert.Equal(OrderStatus.Submitted, Db.Orders.AsNoTracking().Single(o => o.Id == id).Status);
    }
}
