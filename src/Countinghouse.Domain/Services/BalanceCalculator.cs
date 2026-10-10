using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;

namespace Countinghouse.Domain.Services;

public static class BalanceCalculator
{
    // For accounts without a SimpleFIN connection, CurrentBalance is always derived:
    // OpeningBalance + sum of all transactions. Synced accounts get their balance from
    // SimpleFIN directly and are left alone here.
    // Amounts are summed in memory: SQLite can't aggregate decimal server-side.
    public static async Task RecalculateAsync(CountinghouseContext db, int accountId)
    {
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        if (account.SimpleFinConnectionId is not null)
            return;

        var amounts = await db.Transactions
            .Where(t => t.AccountId == accountId)
            .Select(t => t.Amount)
            .ToListAsync();

        account.CurrentBalance = account.OpeningBalance + amounts.Sum();
        account.BalanceAsOf = DateTime.Now;
        await db.SaveChangesAsync();

        // Every manual balance change funnels through here — transaction add/edit/delete, account
        // edits, the mortgage's balance update, reconciliation adjustments — so this is the one
        // place a snapshot has to be taken to cover all of them. Synced accounts returned above
        // and are snapshotted by the sync itself, which is the only thing that moves their balance.
        await BalanceSnapshotWriter.RecordAsync(db, account);
    }
}
