using Microsoft.EntityFrameworkCore;

namespace RemoveBg.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Email).HasMaxLength(320);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.SecretHash).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.Prefix).HasMaxLength(24);
            entity.Property(x => x.SecretHash).HasMaxLength(64);
            entity.HasOne(x => x.Account).WithMany(x => x.ApiKeys).HasForeignKey(x => x.AccountId);
        });

        modelBuilder.Entity<UsageRecord>(entity =>
        {
            entity.ToTable("usage_records");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.RequestId).IsUnique();
            entity.HasIndex(x => new { x.AccountId, x.CreatedAt });
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.Filename).HasMaxLength(255);
            entity.Property(x => x.ErrorCode).HasMaxLength(80);
        });

        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.ToTable("ledger_entries");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.CreatedAt });
            entity.Property(x => x.Kind).HasMaxLength(30);
        });
    }
}
