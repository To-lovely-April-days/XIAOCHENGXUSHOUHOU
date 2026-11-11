using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// v1.2 工单附件：添加 / 删除（软删除）/ 切换「仅销售和主管可见」（CHANGES-v1.2 §4）；v1.3：附件类别（添加时选、事后可改）。权限判断在 AttachmentRules。
/// 与 OrderService 共用加载、状态记录等内部方法，故以 partial 并入 OrderService。
/// </summary>
public partial class OrderService
{
    /// <summary>
    /// 请求里的附件项：去重（同一 fileId 取第一次的 restricted 与 type）；有项缺 fileId 返回 null（由调用方报各自的文案）；
    /// 任一项 type 非法 → 40000「附件类别不正确」（同一项两者都有问题时先报缺 fileId）。type 省略 / 空白 = Other。
    /// </summary>
    private static List<(int FileId, bool Restricted, AttachmentType Type)>? NormalizeAttachments(List<AttachmentItem>? items)
    {
        var result = new List<(int, bool, AttachmentType)>();
        var seen = new HashSet<int>();
        foreach (var it in items ?? new())
        {
            if (it?.FileId == null) return null;
            if (!AttachmentRules.TryParseType(it.Type, out var type)) throw BizException.BadRequest("附件类别不正确");
            if (seen.Add(it.FileId.Value)) result.Add((it.FileId.Value, it.Restricted == true, type ?? AttachmentType.Other));
        }
        return result;
    }

    /// <summary>§4.4 第 8 步：逐个文件校验，按请求顺序返回实体（已跟踪）</summary>
    private async Task<List<StoredFile>> LoadNewAttachments(List<(int FileId, bool Restricted, AttachmentType Type)> items, CurrentUser user)
    {
        var ids = items.Select(x => x.FileId).ToList();
        var map = (await _db.Files.Where(f => ids.Contains(f.Id)).ToListAsync()).ToDictionary(f => f.Id);
        var files = new List<StoredFile>();
        foreach (var id in ids)
        {
            // 已软删除的文件被全局过滤器排除 → 不存在
            // 头像文件（上传时 category=Avatar 或已被设为头像）不能当附件：否则同一个文件既是头像又是工单附件
            if (!map.TryGetValue(id, out var f) || f.Category == FileCategory.Avatar) throw BizException.BadRequest("附件不存在，请重新上传");
            if (f.UploaderId != user.Id) throw BizException.BadRequest("附件不是本人上传，请重新上传");
            if (f.OrderId != null) throw BizException.BadRequest("附件已被使用，请重新上传");
            files.Add(f);
        }
        return files;
    }

    private Task<int> AttachmentCount(int orderId) =>
        _db.Files.CountAsync(f => f.OrderId == orderId && (f.Category == FileCategory.OrderPhoto || f.Category == FileCategory.Attachment));

    private static void Mount(List<StoredFile> files, List<(int FileId, bool Restricted, AttachmentType Type)> items, int orderId)
    {
        for (var i = 0; i < files.Count; i++)
        {
            files[i].OrderId = orderId;
            files[i].Category = FileCategory.Attachment;
            files[i].Restricted = items[i].Restricted;
            files[i].AttachmentType = items[i].Type;
        }
    }

    private static string ShortName(StoredFile f)
    {
        var name = FileService.NameOf(f);
        return name.Length > 40 ? name[..39] + "…" : name;
    }

    /// <summary>「{姓名} 上传附件 {名单}[ 等 n 个]」：名单取前 3 个</summary>
    private static string UploadLogText(string actor, List<StoredFile> files) =>
        $"{actor} 上传附件 {string.Join("、", files.Take(3).Select(ShortName))}" + (files.Count > 3 ? $" 等 {files.Count} 个" : "");

    public async Task<OrderDetail> AddAttachments(int id, AttachmentsRequest r, CurrentUser user)
    {
        // 1. 请求体
        var items = NormalizeAttachments(r.Attachments);
        if (items == null || items.Count == 0) throw BizException.BadRequest("请选择要添加的附件");

        // 2–9 在写锁事务内：并发添加不会突破上限
        await using var tx = await WriteLock();
        var o = await Load(id);
        OrderViewBuilder.EnsureCanView(o, user);
        var denied = AttachmentRules.AddDenied(o, user);
        if (denied != null) throw denied;
        if (items.Any(x => x.Restricted) && !AttachmentRules.CanRestrict(user)) throw BizException.Forbidden(AttachmentRules.RestrictDenied);
        var files = await LoadNewAttachments(items, user);
        var left = AttachmentRules.Limit - await AttachmentCount(id);
        if (files.Count > left)
            throw BizException.BadRequest(left <= 0
                ? $"附件最多 {AttachmentRules.Limit} 个，本单已达上限"
                : $"附件最多 {AttachmentRules.Limit} 个，本单还可添加 {left} 个");

        Mount(files, items, id);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var open = files.Where(f => !f.Restricted).ToList();
        var restricted = files.Where(f => f.Restricted).ToList();
        if (open.Count > 0) AddLog(o, me, UploadLogText(me.Name, open), o.Status, now, LogKind.Attachment);
        if (restricted.Count > 0)
            AddLog(o, me, UploadLogText(me.Name, restricted) + "（仅销售和主管可见）", o.Status, now, LogKind.AttachmentPrivate);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return await _view.BuildDetail(id, user);
    }

    /// <summary>本单未删除、且对当前用户可见的附件；否则 40400「附件不存在」</summary>
    private async Task<StoredFile> LoadAttachment(int orderId, int fileId, CurrentUser user)
    {
        var f = await _db.Files.FirstOrDefaultAsync(x => x.Id == fileId && x.OrderId == orderId);
        if (f == null || !AttachmentRules.IsAttachment(f.Category) || !AttachmentRules.Visible(f, user))
            throw BizException.NotFound("附件不存在");
        return f;
    }

    public async Task<OrderDetail> DeleteAttachment(int id, int fileId, CurrentUser user)
    {
        await using var tx = await WriteLock();
        var o = await Load(id);
        OrderViewBuilder.EnsureCanView(o, user);
        var f = await LoadAttachment(id, fileId, user);
        var denied = AttachmentRules.DeleteDenied(f, o, user);
        if (denied != null) throw denied;

        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        // 软删除：OrderId 与磁盘文件保留
        f.DeletedAt = now;
        f.DeletedById = me.Id;
        AddLog(o, me, $"{me.Name} 删除附件 {FileService.NameOf(f)}", o.Status, now,
            f.Restricted ? LogKind.AttachmentPrivate : LogKind.Attachment);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return await _view.BuildDetail(id, user);
    }

    public async Task<OrderDetail> SetAttachmentRestricted(int id, int fileId, RestrictedRequest r, CurrentUser user)
    {
        EnsureRole(user, Role.Supervisor, "只有主管可以修改附件可见范围");
        if (r.Restricted == null) throw BizException.BadRequest("请选择可见范围");
        await using var tx = await WriteLock();
        var o = await Load(id);
        var f = await LoadAttachment(id, fileId, user);
        if (f.Restricted == r.Restricted.Value) return await _view.BuildDetail(id, user);

        var me = await _view.LoadEmployee(user.Id);
        f.Restricted = r.Restricted.Value;
        var name = FileService.NameOf(f);
        if (f.Restricted)
        {
            // 该附件公开期间留下的记录（上传 / 取消受限 / …）里带着文件名：一并改为受限记录，
            // 否则工程师（包括之后才接手的）仍能从状态记录里看到受限附件的名称。
            // 按名称匹配、只收紧不放开：同一条记录里的其他公开文件名会一起对工程师隐藏；取消受限后旧记录不恢复。
            var shown = ShortName(f);
            var earlier = await _db.StatusLogs.Where(l => l.OrderId == id && l.Kind == LogKind.Attachment).ToListAsync();
            foreach (var l in earlier.Where(l => l.Text.Contains(name) || l.Text.Contains(shown))) l.Kind = LogKind.AttachmentPrivate;
            AddLog(o, me, $"{me.Name} 将附件 {name} 设为仅销售和主管可见", o.Status, _clock.Now, LogKind.AttachmentPrivate);
        }
        else
            AddLog(o, me, $"{me.Name} 将附件 {name} 设为所有人可见", o.Status, _clock.Now, LogKind.Attachment);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return await _view.BuildDetail(id, user);
    }

    /// <summary>
    /// v1.3：修改附件类别（上传人本人或主管；传错类时不用删了重传）。任何角色都可调用，按 AttachmentRules.ChangeTypeDenied 判权。
    /// </summary>
    public async Task<OrderDetail> SetAttachmentType(int id, int fileId, AttachmentTypeRequest r, CurrentUser user)
    {
        // 1. 请求体：缺失（空白）与非法值同一文案
        if (!AttachmentRules.TryParseType(r.Type, out var parsed) || parsed == null) throw BizException.BadRequest("请选择附件类别");
        var type = parsed.Value;

        await using var tx = await WriteLock();
        var o = await Load(id);
        OrderViewBuilder.EnsureCanView(o, user);
        var f = await LoadAttachment(id, fileId, user);
        var denied = AttachmentRules.ChangeTypeDenied(f, o, user);
        if (denied != null) throw denied;
        if (f.AttachmentType == type) return await _view.BuildDetail(id, user);

        var me = await _view.LoadEmployee(user.Id);
        f.AttachmentType = type;
        // 文件名不截短：与「设为受限时收起旧记录」的按名称匹配逻辑保持兼容
        AddLog(o, me, $"{me.Name} 将附件 {FileService.NameOf(f)} 归为「{EnumText.AttachmentShort(type)}」", o.Status, _clock.Now,
            f.Restricted ? LogKind.AttachmentPrivate : LogKind.Attachment);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return await _view.BuildDetail(id, user);
    }
}
