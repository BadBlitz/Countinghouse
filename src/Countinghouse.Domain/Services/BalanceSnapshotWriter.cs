using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Services;

// Writes the AccountBalanceSnapshot rows the net-worth report reads: one row per account per day,
// last write of the day winning.
//
// Snapshots can only ever be collected going forward. SimpleFIN serves today's balance, not last
// month's, and a manual account's history is only recoverable as far back as its transactions go —
// so a day that passes unrecorded is gone. That's why every path that changes a balance records
// one, and why startup records a row for every account: a quiet day with no sync and no edits
// still deserves a point on the chart.
public static class BalanceSnapshotWriter
{
    // Callers pass their own DbContext, exactly as BalanceCalculator does, so the snapshot lands
    // in the same unit of work as the balance it describes.
    public static async Task RecordAsync(
        CountinghouseContext db, int accountId, CancellationToken cancellationToken = default)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is not null)
            await RecordAsync(db, account, cancellationToken);
    }

    public static async Task RecordAsync(
        CountinghouseContext db, Account account, CancellationToken cancellationToken = default)
    {
        // BalanceAsOf is the bank's own timestamp on a synced account and "when the arithmetic last
        // ran" on a manual one. Either dates the balance better than Today would: a sync against a
        // feed that hasn't refreshed since yesterday shouldn't file its figure under today.
        var asOf = DateOnly.FromDateTime(account.BalanceAsOf ?? DateTime.Now);

        var existing = await db.AccountBalanceSnapshots
            .FirstOrDefaultAsync(s => s.AccountId == account.Id && s.AsOfDate == asOf, cancellationToken);

        // Overwrite rather than skip. Several balances can pass through one day and the last one
        // is the one that day ended on; skipping would freeze the day at its first sync instead.
        if (existing is not null)
            existing.Balance = account.CurrentBalance;
        else
            db.AccountBalanceSnapshots.Add(new AccountBalanceSnapshot
            {
                AccountId = account.Id,
                AsOfDate = asOf,
                Balance = account.CurrentBalance,
            });

        await db.SaveChangesAsync(cancellationToken);
    }

    // Startup path. Closed accounts are skipped: they contribute nothing to net worth and would
    // otherwise accrue a row a day forever at a balance that can no longer change.
    public static async Task<int> RecordAllAsync(
        CountinghouseContext db, CancellationToken cancellationToken = default)
    {
        var accounts = await db.Accounts.Where(a => !a.IsClosed).ToListAsync(cancellationToken);
        foreach (var account in accounts)
            await RecordAsync(db, account, cancellationToken);
        return accounts.Count;
    }
}
