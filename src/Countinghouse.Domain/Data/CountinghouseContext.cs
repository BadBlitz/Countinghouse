using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Data;

public class CountinghouseContext(DbContextOptions<CountinghouseContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<SimpleFinConnection> SimpleFinConnections => Set<SimpleFinConnection>();
    public DbSet<ReconciliationSession> ReconciliationSessions => Set<ReconciliationSession>();
    public DbSet<AccountBalanceSnapshot> AccountBalanceSnapshots => Set<AccountBalanceSnapshot>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(account =>
        {
            account.Property(a => a.Name).IsRequired();
            account.HasIndex(a => new { a.SimpleFinConnectionId, a.SimpleFinAccountId }).IsUnique();
            account.HasOne(a => a.SimpleFinConnection)
                .WithMany(c => c.Accounts)
                .HasForeignKey(a => a.SimpleFinConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Transaction>(txn =>
        {
            txn.Property(t => t.Payee).IsRequired();
            // Sync dedup key; SQLite treats NULLs as distinct, so manual rows don't collide.
            txn.HasIndex(t => new { t.AccountId, t.ExternalId }).IsUnique();
            txn.HasIndex(t => t.PostedDate);
            txn.HasOne(t => t.Account)
                .WithMany(a => a.Transactions)
                .HasForeignKey(t => t.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            txn.HasOne(t => t.Category)
                .WithMany()
                .HasForeignKey(t => t.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
            txn.HasOne(t => t.ReconciliationSession)
                .WithMany(s => s.Transactions)
                .HasForeignKey(t => t.ReconciliationSessionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Category>(category =>
        {
            category.Property(c => c.Name).IsRequired();
            category.HasIndex(c => new { c.ParentCategoryId, c.Name }).IsUnique();
            category.HasOne(c => c.ParentCategory)
                .WithMany(c => c.Subcategories)
                .HasForeignKey(c => c.ParentCategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReconciliationSession>()
            .HasOne(s => s.Account)
            .WithMany()
            .HasForeignKey(s => s.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AppSetting>(setting =>
        {
            setting.HasKey(s => s.Key);
            setting.Property(s => s.Value).IsRequired();
        });

        modelBuilder.Entity<BackupRun>(run => run.HasIndex(r => r.CompletedAt));

        modelBuilder.Entity<CategoryRule>(rule =>
        {
            rule.Property(r => r.Pattern).IsRequired();
            rule.HasIndex(r => new { r.MatchType, r.Pattern }).IsUnique();
            // Restrict, not Cascade: deleting a category should fail loudly while rules point at
            // it rather than silently taking the rules down with it.
            rule.HasOne(r => r.Category)
                .WithMany()
                .HasForeignKey(r => r.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountBalanceSnapshot>(snapshot =>
        {
            snapshot.HasIndex(s => new { s.AccountId, s.AsOfDate }).IsUnique();
            snapshot.HasOne(s => s.Account)
                .WithMany()
                .HasForeignKey(s => s.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
