using Microsoft.EntityFrameworkCore;
using SogutmaHakedisKontrol.Domain.Entities;

namespace SogutmaHakedisKontrol.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<UnitPriceList> UnitPriceLists => Set<UnitPriceList>();
    public DbSet<UnitPriceItem> UnitPriceItems => Set<UnitPriceItem>();
    public DbSet<UnitPriceItemAuditLog> UnitPriceItemAuditLogs => Set<UnitPriceItemAuditLog>();
    public DbSet<MaterialAlias> MaterialAliases => Set<MaterialAlias>();
    public DbSet<ProgressPaymentCheck> ProgressPaymentChecks => Set<ProgressPaymentCheck>();
    public DbSet<ProgressPaymentCheckItem> ProgressPaymentCheckItems => Set<ProgressPaymentCheckItem>();

    // ── Mağaza ana listesi + AI belge analizi ───────────────────────────
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<AiAnalysisJob> AiAnalysisJobs => Set<AiAnalysisJob>();
    public DbSet<AiDocumentPage> AiDocumentPages => Set<AiDocumentPage>();
    public DbSet<AiPageEmployee> AiPageEmployees => Set<AiPageEmployee>();
    public DbSet<AiPageMaterial> AiPageMaterials => Set<AiPageMaterial>();
    public DbSet<AiComparisonResult> AiComparisonResults => Set<AiComparisonResult>();
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();
    public DbSet<AiSourceDocument> AiSourceDocuments => Set<AiSourceDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UnitPriceList>(e =>
        {
            e.Property(l => l.CompanyName).HasMaxLength(200).IsRequired();
            e.Property(l => l.Region).HasMaxLength(200).IsRequired();
            e.Property(l => l.Name).HasMaxLength(300).IsRequired();
            e.Property(l => l.SourceFileName).HasMaxLength(500);
        });

        modelBuilder.Entity<UnitPriceItem>(e =>
        {
            e.Property(i => i.ItemCode).HasMaxLength(50);
            e.Property(i => i.Category).HasMaxLength(200);
            e.Property(i => i.MaterialName).HasMaxLength(500).IsRequired();
            e.Property(i => i.Brand).HasMaxLength(300);
            e.Property(i => i.Spec).HasMaxLength(300);
            e.Property(i => i.Unit).HasMaxLength(50);
            e.Property(i => i.Price).HasColumnType("decimal(18,4)");
            e.Property(i => i.Currency).HasMaxLength(10).IsRequired();
            e.Property(i => i.NormalizedName).HasMaxLength(500).IsRequired();
            e.Property(i => i.SourceFileName).HasMaxLength(500);
            e.HasIndex(i => i.NormalizedName);

            e.HasOne(i => i.UnitPriceList)
                .WithMany(l => l.Items)
                .HasForeignKey(i => i.UnitPriceListId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UnitPriceItemAuditLog>(e =>
        {
            e.Property(a => a.FieldName).HasMaxLength(100).IsRequired();
            e.Property(a => a.OldValue).HasMaxLength(500);
            e.Property(a => a.NewValue).HasMaxLength(500);
            e.Property(a => a.Note).HasMaxLength(1000).IsRequired();
        });

        modelBuilder.Entity<MaterialAlias>(e =>
        {
            e.Property(a => a.CompanyName).HasMaxLength(200);
            e.Property(a => a.AliasText).HasMaxLength(500).IsRequired();
            e.Property(a => a.NormalizedAlias).HasMaxLength(500).IsRequired();
            e.Property(a => a.Note).HasMaxLength(500);
            e.HasIndex(a => new { a.CompanyName, a.NormalizedAlias });

            e.HasOne(a => a.UnitPriceItem)
                .WithMany(i => i.Aliases)
                .HasForeignKey(a => a.UnitPriceItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProgressPaymentCheck>(e =>
        {
            e.Property(c => c.CompanyName).HasMaxLength(200).IsRequired();
            e.Property(c => c.Region).HasMaxLength(200).IsRequired();
            e.Property(c => c.ClaimTypeName).HasMaxLength(100).IsRequired();
            e.Property(c => c.PeriodLabel).HasMaxLength(50).IsRequired();
            e.Property(c => c.OriginalFileName).HasMaxLength(500).IsRequired();
            e.Property(c => c.OriginalFilePath).HasMaxLength(1000).IsRequired();
            e.Property(c => c.ControlledFilePath).HasMaxLength(1000);
            e.Property(c => c.ExchangeRateEur).HasColumnType("decimal(18,4)");
            e.Property(c => c.CompanyTotal).HasColumnType("decimal(18,2)");
            e.Property(c => c.CalculatedTotal).HasColumnType("decimal(18,2)");
            e.Property(c => c.Difference).HasColumnType("decimal(18,2)");
            e.Property(c => c.Status).HasConversion<int>();

            e.HasOne(c => c.UnitPriceList)
                .WithMany()
                .HasForeignKey(c => c.UnitPriceListId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProgressPaymentCheckItem>(e =>
        {
            e.Property(i => i.SheetName).HasMaxLength(100);
            e.Property(i => i.StoreCode).HasMaxLength(50);
            e.Property(i => i.StoreName).HasMaxLength(300);
            e.Property(i => i.StoreFormat).HasMaxLength(100);
            e.Property(i => i.MaintenanceFormNo).HasMaxLength(100);
            e.Property(i => i.OriginalItemCode).HasMaxLength(100);
            e.Property(i => i.OriginalMaterialName).HasMaxLength(500).IsRequired();
            e.Property(i => i.OriginalMaterialSpec).HasMaxLength(300);
            e.Property(i => i.Unit).HasMaxLength(50);
            e.Property(i => i.CompanyUnitPrice).HasColumnType("decimal(18,4)");
            e.Property(i => i.CompanyLineTotal).HasColumnType("decimal(18,2)");
            e.Property(i => i.Quantity).HasColumnType("decimal(18,4)");
            e.Property(i => i.MatchedMaterialName).HasMaxLength(500);
            e.Property(i => i.MatchConfidence).HasColumnType("decimal(5,4)");
            e.Property(i => i.ApprovedUnitPrice).HasColumnType("decimal(18,4)");
            e.Property(i => i.ApprovedCurrency).HasMaxLength(10);
            e.Property(i => i.ApprovedUnitPriceTry).HasColumnType("decimal(18,4)");
            e.Property(i => i.CalculatedLineTotal).HasColumnType("decimal(18,2)");
            e.Property(i => i.Difference).HasColumnType("decimal(18,2)");
            e.Property(i => i.DifferencePercent).HasColumnType("decimal(9,2)");
            e.Property(i => i.ControlNote).HasMaxLength(1000);
            e.Property(i => i.MatchStatus).HasConversion<int>();
            e.Property(i => i.ControlStatus).HasConversion<int>();

            e.HasOne(i => i.ProgressPaymentCheck)
                .WithMany(c => c.Items)
                .HasForeignKey(i => i.ProgressPaymentCheckId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Mağaza ana listesi ────────────────────────────────────────────
        modelBuilder.Entity<Store>(e =>
        {
            e.Property(s => s.CompanyName).HasMaxLength(200).IsRequired();
            e.Property(s => s.Region).HasMaxLength(200).IsRequired();
            e.Property(s => s.Code).HasMaxLength(50).IsRequired();
            e.Property(s => s.Name).HasMaxLength(300).IsRequired();
            e.Property(s => s.City).HasMaxLength(100);
            e.Property(s => s.StoreRegion).HasMaxLength(100);
            e.Property(s => s.Address).HasMaxLength(500);
            e.Property(s => s.NormalizedCode).HasMaxLength(50).IsRequired();
            e.Property(s => s.NormalizedName).HasMaxLength(300).IsRequired();
            e.HasIndex(s => new { s.CompanyName, s.Region, s.NormalizedCode });
            e.HasIndex(s => s.NormalizedName);
        });

        // ── AI belge analizi ──────────────────────────────────────────────
        modelBuilder.Entity<AiAnalysisJob>(e =>
        {
            e.Property(j => j.ServiceFormsFileName).HasMaxLength(500);
            e.Property(j => j.ServiceFormsFilePath).HasMaxLength(1000);
            e.Property(j => j.MaintenanceFormsFileName).HasMaxLength(500);
            e.Property(j => j.MaintenanceFormsFilePath).HasMaxLength(1000);
            e.Property(j => j.CurrentStepDescription).HasMaxLength(300);
            e.Property(j => j.ErrorMessage).HasMaxLength(2000);
            e.Property(j => j.Status).HasConversion<int>();

            e.HasOne(j => j.ProgressPaymentCheck)
                .WithMany()
                .HasForeignKey(j => j.ProgressPaymentCheckId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiDocumentPage>(e =>
        {
            e.Property(p => p.StoreCodeRaw).HasMaxLength(100);
            e.Property(p => p.StoreNameRaw).HasMaxLength(300);
            e.Property(p => p.StoreConfidence).HasColumnType("decimal(5,4)");
            e.Property(p => p.FormNumber).HasMaxLength(100);
            e.Property(p => p.DescriptionRaw).HasMaxLength(2000);
            e.Property(p => p.WorkPerformedRaw).HasMaxLength(2000);
            e.Property(p => p.FormTotalHoursRaw).HasColumnType("decimal(9,2)");
            e.Property(p => p.CalculatedManHours).HasColumnType("decimal(9,2)");
            e.Property(p => p.PayableManHours).HasColumnType("decimal(9,2)");
            e.Property(p => p.RawResponseJson).HasColumnType("TEXT");
            e.Property(p => p.ErrorMessage).HasMaxLength(2000);
            e.Property(p => p.ManualReviewReason).HasMaxLength(500);
            e.Property(p => p.SourceKind).HasConversion<int>();
            e.Property(p => p.Status).HasConversion<int>();
            e.Property(p => p.DocumentType).HasConversion<int>();
            e.Property(p => p.StoreMatchMethod).HasConversion<int>();
            e.HasIndex(p => new { p.JobId, p.SourceKind, p.PageNumber });

            e.HasOne(p => p.Job)
                .WithMany(j => j.Pages)
                .HasForeignKey(p => p.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiPageEmployee>(e =>
        {
            e.Property(x => x.NameRaw).HasMaxLength(200);
            e.Property(x => x.StartTimeRaw).HasMaxLength(20);
            e.Property(x => x.EndTimeRaw).HasMaxLength(20);
            e.Property(x => x.HoursWorked).HasColumnType("decimal(9,2)");
            e.Property(x => x.Confidence).HasColumnType("decimal(5,4)");

            e.HasOne(x => x.Page)
                .WithMany(p => p.Employees)
                .HasForeignKey(x => x.PageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiPageMaterial>(e =>
        {
            e.Property(x => x.RawName).HasMaxLength(500).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(500);
            e.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
            e.Property(x => x.Unit).HasMaxLength(50);
            e.Property(x => x.Confidence).HasColumnType("decimal(5,4)");
            e.Property(x => x.UserCorrectedQuantity).HasColumnType("decimal(18,4)");
            e.Property(x => x.UserCorrectedUnit).HasMaxLength(50);
            e.Property(x => x.CorrectionNote).HasMaxLength(500);

            e.HasOne(x => x.Page)
                .WithMany(p => p.Materials)
                .HasForeignKey(x => x.PageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiComparisonResult>(e =>
        {
            e.Property(x => x.StoreLabel).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            e.Property(x => x.FormValue).HasMaxLength(300);
            e.Property(x => x.HakedisValue).HasMaxLength(300);
            e.Property(x => x.Explanation).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ItemType).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => x.JobId);

            e.HasOne(x => x.Job)
                .WithMany(j => j.ComparisonResults)
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiUsageLog>(e =>
        {
            e.Property(x => x.Model).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<AiSourceDocument>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(500).IsRequired();
            e.Property(x => x.FilePath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.SourceKind).HasConversion<int>();
            e.HasIndex(x => new { x.JobId, x.SourceKind, x.PageOffset });

            e.HasOne(x => x.Job)
                .WithMany()
                .HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
