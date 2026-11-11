using Huotong.AfterSales.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<WorkOrder> Orders => Set<WorkOrder>();
    public DbSet<Punch> Punches => Set<Punch>();
    public DbSet<StatusLog> StatusLogs => Set<StatusLog>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<OrderAssistant> OrderAssistants => Set<OrderAssistant>();
    public DbSet<OrderRequest> OrderRequests => Set<OrderRequest>();
    public DbSet<PriceSuggestion> PriceSuggestions => Set<PriceSuggestion>();
    public DbSet<FactoryRecord> FactoryRecords => Set<FactoryRecord>();
    public DbSet<OrderDevice> OrderDevices => Set<OrderDevice>();
    public DbSet<OrderTransfer> OrderTransfers => Set<OrderTransfer>();
    public DbSet<OrderExit> OrderExits => Set<OrderExit>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Employee>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.Department).HasMaxLength(50);
            e.Property(x => x.OpenId).HasMaxLength(64);
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Phone).IsUnique();
            e.HasIndex(x => x.OpenId);
            e.HasOne(x => x.AvatarFile).WithMany().HasForeignKey(x => x.AvatarFileId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.Surname);
        });

        b.Entity<WorkOrder>(e =>
        {
            e.ToTable("WorkOrders");
            e.Property(x => x.OrderNo).HasMaxLength(20);
            e.HasIndex(x => x.OrderNo).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProblemType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Urgency).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ServiceType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RunStatus).HasConversion<string>().HasMaxLength(20);
            // SQLite 不支持 decimal 排序/聚合，用 double 存储
            e.Property(x => x.Price).HasConversion<double>();
            // PriceSet：不要 HasDefaultValue(true)（EF 会吞掉显式的 false）；列默认值只写在迁移里
            e.Property(x => x.HoldReasonType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.HoldFromStatus).HasConversion<string>().HasMaxLength(20);
            // v1.3：DeviceText / PendingTransferToId 都不要 HasDefaultValue；PendingTransferToId 是普通整数列（无外键）
            e.Property(x => x.DeviceText).HasMaxLength(250);
            e.HasIndex(x => x.PendingTransferToId);
            e.HasOne(x => x.Creator).WithMany().HasForeignKey(x => x.CreatorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Assignee).WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Punch>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(x => new { x.EngineerId, x.Time });
            e.HasOne(x => x.Order).WithMany(o => o.Punches).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Engineer).WithMany().HasForeignKey(x => x.EngineerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SupplementBy).WithMany().HasForeignKey(x => x.SupplementById).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PhotoFile).WithMany().HasForeignKey(x => x.PhotoFileId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<StatusLog>(e =>
        {
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Order).WithMany(o => o.Logs).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StoredFile>(e =>
        {
            e.ToTable("StoredFiles");
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Path).HasMaxLength(260);
            e.Property(x => x.MimeType).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            // v1.3：不要 HasDefaultValue（EF 会吞掉与 CLR 默认值相同的显式值）；列默认值 'Other' 只写在迁移里
            e.Property(x => x.AttachmentType).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.CreatedAt);
            // v1.2 软删除：已删除的文件对所有读取路径都视为不存在（需要看到时用 IgnoreQueryFilters）
            e.HasQueryFilter(x => x.DeletedAt == null);
        });

        b.Entity<OrderAssistant>(e =>
        {
            e.HasIndex(x => new { x.OrderId, x.EngineerId }).IsUnique();
            e.HasIndex(x => x.EngineerId);
            e.HasOne(x => x.Order).WithMany(o => o.Assistants).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Engineer).WithMany().HasForeignKey(x => x.EngineerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AddedBy).WithMany().HasForeignKey(x => x.AddedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OrderRequest>(e =>
        {
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.HoldReasonType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(200);
            e.Property(x => x.Reply).HasMaxLength(200);
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TargetEngineer).WithMany().HasForeignKey(x => x.TargetEngineerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Decider).WithMany().HasForeignKey(x => x.DeciderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PriceSuggestion>(e =>
        {
            e.Property(x => x.Amount).HasConversion<double>();
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasIndex(x => x.OrderId);
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Sales).WithMany().HasForeignKey(x => x.SalesId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<FactoryRecord>(e =>
        {
            e.Property(x => x.Node).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Courier).HasMaxLength(50);
            e.Property(x => x.TrackingNo).HasMaxLength(50);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.OrderId, x.Node }).IsUnique();
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Operator).WithMany().HasForeignKey(x => x.OperatorId).OnDelete(DeleteBehavior.Restrict);
        });

        // ---------- v1.3 ----------

        b.Entity<OrderDevice>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Sn).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(100);
            e.HasIndex(x => x.OrderId);
            e.HasOne(x => x.Order).WithMany(o => o.Devices).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderTransfer>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(200);
            e.Property(x => x.Reply).HasMaxLength(200);
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => new { x.ToEngineerId, x.Status });
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.From).WithMany().HasForeignKey(x => x.FromEngineerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.To).WithMany().HasForeignKey(x => x.ToEngineerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DecidedBy).WithMany().HasForeignKey(x => x.DecidedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OrderExit>(e =>
        {
            e.Property(x => x.Via).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.OrderId, x.EngineerId }).IsUnique();
            e.HasIndex(x => x.EngineerId);
            e.HasOne(x => x.Order).WithMany(o => o.Exits).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Engineer).WithMany().HasForeignKey(x => x.EngineerId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
