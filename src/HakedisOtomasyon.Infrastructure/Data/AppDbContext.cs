using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Store> Stores => Set<Store>();
    public DbSet<PriceItem> PriceItems => Set<PriceItem>();
    public DbSet<ProgressClaim> ProgressClaims => Set<ProgressClaim>();
    public DbSet<ServiceForm> ServiceForms => Set<ServiceForm>();
    public DbSet<ServiceItem> ServiceItems => Set<ServiceItem>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<ExportLog> ExportLogs => Set<ExportLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<PriceAlias> PriceAliases => Set<PriceAlias>();
    public DbSet<ExchangeRateCache> ExchangeRateCaches => Set<ExchangeRateCache>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PriceAlias>(e =>
        {
            e.HasIndex(a => a.NormalizedKeyword).IsUnique();
            e.Property(a => a.Keyword).HasMaxLength(300).IsRequired();
            e.Property(a => a.NormalizedKeyword).HasMaxLength(300).IsRequired();
            e.Property(a => a.TargetText).HasMaxLength(600).IsRequired();
        });

        modelBuilder.Entity<Store>(e =>
        {
            // Mağaza kodu sadece KENDİ disiplini içinde tekil olmalı — Yangın'daki "1000"
            // kodu ile Asansör'deki "1000" kodu aynı anda var olabilmeli.
            e.HasIndex(s => new { s.Discipline, s.Code }).IsUnique();
            e.Property(s => s.Code).HasMaxLength(50).IsRequired();
            e.Property(s => s.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<PriceItem>(e =>
        {
            e.Property(p => p.Description).HasMaxLength(500).IsRequired();
            e.Property(p => p.Unit).HasMaxLength(50);
            e.Property(p => p.SourceSheetName).HasMaxLength(100);
            e.Property(p => p.PozNo).HasMaxLength(50);
            e.Property(p => p.MainCategory).HasMaxLength(300);
            e.Property(p => p.SubCategory).HasMaxLength(300);
            e.Property(p => p.SubCategory2).HasMaxLength(300);
            e.Property(p => p.DisplayName).HasMaxLength(600);
            e.Property(p => p.InvoiceDescription).HasMaxLength(600);
            e.Property(p => p.SearchText).HasMaxLength(2000);
            e.Property(p => p.MaterialPrice).HasColumnType("decimal(18,2)");
            e.Property(p => p.LaborPrice).HasColumnType("decimal(18,2)");
            e.Property(p => p.PriceType).HasConversion<int>();
            e.Property(p => p.CurrencyCode).HasMaxLength(10);
            e.Property(p => p.ListPriceUsd).HasColumnType("decimal(18,4)");
            e.Property(p => p.DiscountRate).HasColumnType("decimal(5,2)");
            e.Property(p => p.DiscountedUsdPrice).HasColumnType("decimal(18,4)");
            e.Ignore(p => p.UnitPrice);
        });

        modelBuilder.Entity<ProgressClaim>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(300).IsRequired();
            e.Property(p => p.CompanyName).HasMaxLength(300).IsRequired();
        });

        modelBuilder.Entity<ServiceForm>(e =>
        {
            e.HasOne(f => f.ProgressClaim)
                .WithMany(c => c.ServiceForms)
                .HasForeignKey(f => f.ProgressClaimId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(f => f.Store)
                .WithMany(s => s.ServiceForms)
                .HasForeignKey(f => f.StoreId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ServiceItem>(e =>
        {
            e.Property(i => i.MaterialPrice).HasColumnType("decimal(18,2)");
            e.Property(i => i.LaborPrice).HasColumnType("decimal(18,2)");
            e.Property(i => i.UnitPrice).HasColumnType("decimal(18,2)");
            e.Property(i => i.TotalPrice).HasColumnType("decimal(18,2)");
            e.Property(i => i.Quantity).HasColumnType("decimal(18,4)");

            e.HasOne(i => i.ServiceForm)
                .WithMany(f => f.ServiceItems)
                .HasForeignKey(i => i.ServiceFormId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(i => i.Invoice)
                .WithMany(inv => inv.ServiceItems)
                .HasForeignKey(i => i.InvoiceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Invoice>(e =>
        {
            e.Property(i => i.InvoiceAmount).HasColumnType("decimal(18,2)");
            e.Property(i => i.Quantity).HasColumnType("decimal(18,4)");
            e.Property(i => i.MarkupRate).HasColumnType("decimal(5,4)");
            e.Property(i => i.CalculatedTotal).HasColumnType("decimal(18,2)");

            e.HasOne(i => i.ServiceForm)
                .WithMany(f => f.Invoices)
                .HasForeignKey(i => i.ServiceFormId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExportLog>(e =>
        {
            e.HasOne(l => l.ProgressClaim)
                .WithMany(c => c.ExportLogs)
                .HasForeignKey(l => l.ProgressClaimId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasIndex(s => s.Key).IsUnique();
            e.Property(s => s.Key).HasMaxLength(100).IsRequired();
            e.Property(s => s.Value).HasMaxLength(2000);
        });

        modelBuilder.Entity<ExchangeRateCache>(e =>
        {
            e.Property(r => r.CurrencyCode).HasMaxLength(10).IsRequired();
            e.Property(r => r.Source).HasMaxLength(50).IsRequired();
            e.Property(r => r.ForexBuying).HasColumnType("decimal(18,4)");
            e.Property(r => r.ForexSelling).HasColumnType("decimal(18,4)");
            e.HasIndex(r => new { r.CurrencyCode, r.RateDate }).IsUnique();
        });
    }
}
