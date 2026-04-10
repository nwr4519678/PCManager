using Microsoft.EntityFrameworkCore;
using PCManager.Core.Models;

namespace PCManager.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<ResourceLog> ResourceLogs { get; set; } = null!;
    public DbSet<SecurityEvent> SecurityEvents { get; set; } = null!;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.Entity<ResourceLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Timestamp).IsRequired();
        });

        modelBuilder.Entity<SecurityEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Timestamp).IsRequired();
            entity.Property(e => e.EventType).HasMaxLength(100);
        });
    }
}
