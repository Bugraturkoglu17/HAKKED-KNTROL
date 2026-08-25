using Microsoft.EntityFrameworkCore;
using SogutmaHakedisKontrol.Infrastructure.Data;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Her teste özel, izole geçici SQLite veritabanı — testler birbirini etkilemez.</summary>
internal static class TestDbFactory
{
    public static AppDbContext Create()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sogutma_test_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
