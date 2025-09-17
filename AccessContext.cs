using Microsoft.EntityFrameworkCore;
using System;
using System.IO;

namespace SpaceAccess.Wpf;

public class AccessContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<ScanEvent> Events => Set<ScanEvent>();

    private static string DbPath()
    {
        var app = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(app, "SpaceAccess");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "access.sqlite");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={DbPath()};Cache=Shared");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasIndex(u => u.StudentId).IsUnique();
        modelBuilder.Entity<ScanEvent>().HasIndex(e => new { e.LocationId, e.TimestampUtc });
        modelBuilder.Entity<Location>().HasData(new Location { Id = SeedLocationId, Name = "Main Space", Capacity = 100 });
    }

    public static Guid SeedLocationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static void EnsureCreatedAndSeed()
    {
        using var db = new AccessContext();
        db.Database.EnsureCreated();
        if (db.Locations.Find(SeedLocationId) == null)
        {
            db.Locations.Add(new Location { Id = SeedLocationId, Name = "Main Space", Capacity = 100 });
            db.SaveChanges();
        }
    }
}
