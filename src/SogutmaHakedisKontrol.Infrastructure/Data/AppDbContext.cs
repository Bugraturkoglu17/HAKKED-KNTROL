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
    }
}
