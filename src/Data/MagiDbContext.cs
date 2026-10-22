using Magi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Magi.Api.Data;

public sealed class MagiDbContext(DbContextOptions<MagiDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Deliberation> Deliberations => Set<Deliberation>();
    public DbSet<CoreVote> CoreVotes => Set<CoreVote>();
    public DbSet<ProviderSetting> ProviderSettings => Set<ProviderSetting>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.PasswordHash).HasMaxLength(100);
        });

        model.Entity<Deliberation>(e =>
        {
            e.HasIndex(d => new { d.UserId, d.CreatedAt });
            e.Property(d => d.Proposition).HasMaxLength(2000);
            e.Property(d => d.Context).HasColumnType("text");
            e.Property(d => d.FailureReason).HasMaxLength(1000);
            // Enums as strings: readable in the DB and safe against reordering.
            e.Property(d => d.Mode).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.Resolution).HasConversion<string>().HasMaxLength(20);
            e.HasOne(d => d.User).WithMany(u => u.Deliberations).HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CoreVote>(e =>
        {
            e.HasIndex(v => new { v.DeliberationId, v.Round, v.Core }).IsUnique();
            e.Property(v => v.Core).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.Verdict).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.Provider).HasMaxLength(40);
            e.Property(v => v.Model).HasMaxLength(100);
            e.Property(v => v.Summary).HasMaxLength(1000);
            e.Property(v => v.Reasoning).HasColumnType("text");
            e.Property(v => v.ConditionsJson).HasColumnType("text");
            e.Property(v => v.Error).HasMaxLength(1000);
            e.HasOne(v => v.Deliberation).WithMany(d => d.Votes).HasForeignKey(v => v.DeliberationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ProviderSetting>(e =>
        {
            e.HasKey(p => p.Core);
            e.Property(p => p.Core).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.ApiKeyCipher).HasMaxLength(2000);
            e.Property(p => p.ApiKeyHint).HasMaxLength(8);
            e.Property(p => p.Model).HasMaxLength(100);
            e.Property(p => p.Effort).HasMaxLength(20);
        });
    }
}
