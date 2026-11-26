using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

/// <summary>v2.1 每日工作内容的公共规则（工单服务和打卡服务共用）</summary>
public static class WorkLogRules
{
    public const int MaxLength = 1000;
    public const string LeaveRequired = "请先填写今天的工作内容";
    public const string CompleteRequired = "请填写工作内容";

    /// <summary>内容去空白；空 → null；超长抛错</summary>
    public static string? Clean(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var s = content.Trim();
        if (s.Length > MaxLength) throw BizException.BadRequest($"工作内容不能超过 {MaxLength} 个字");
        return s;
    }

    public static Task<WorkLog?> Today(AppDbContext db, int orderId, int engineerId, DateTime now) =>
        db.WorkLogs.FirstOrDefaultAsync(x => x.OrderId == orderId && x.EngineerId == engineerId && x.Date == now.Date);

    /// <summary>写入或更新某人今天在这张单上的工作内容（由调用方保存）</summary>
    public static async Task<WorkLog> Upsert(AppDbContext db, int orderId, int engineerId, string content, DateTime now)
    {
        var log = await Today(db, orderId, engineerId, now);
        if (log == null)
        {
            log = new WorkLog { OrderId = orderId, EngineerId = engineerId, Date = now.Date, Content = content, CreatedAt = now };
            db.WorkLogs.Add(log);
        }
        else if (log.Content != content)
        {
            log.Content = content;
            log.UpdatedAt = now;
        }
        return log;
    }
}
