using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Services;

public sealed record RegisterRow(Transaction Transaction, decimal RunningBalance);

public class TransactionService(IDbContextFactory<CountinghouseContext> factory)
{
    // Register rows in ascending date order with a running balance that starts at the
    // account's opening balance. Callers wanting newest-first display should reverse.
    public async Task<List<RegisterRow>> GetRegisterAsync(int accountId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var account = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == accountId);
        var transactions = await db.Transactions.AsNoTracking()
            // ParentCategory too: Category.FullName renders "Parent:Child" and silently
            // degrades to just "Child" if the parent isn't loaded.
            .Include(t => t.Category).ThenInclude(c => c!.ParentCategory)
            .Where(t => t.AccountId == accountId)
            .OrderBy(t => t.PostedDate).ThenBy(t => t.Id)
            .ToListAsync();

        var rows = new List<RegisterRow>(transactions.Count);
        var balance = account.OpeningBalance;
        foreach (var txn in transactions)
        {
            balance += txn.Amount;
            rows.Add(new RegisterRow(txn, balance));
        }
        return rows;
    }

    public async Task<List<Transaction>> GetRecentAsync(int count = 10)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking()
            .Include(t => t.Account)
            .Include(t => t.Category).ThenInclude(c => c!.ParentCategory)
            .OrderByDescending(t => t.PostedDate).ThenByDescending(t => t.Id)
            .Take(count)
            .ToListAsync();
    }

    public async Task<Transaction> AddTransactionAsync(Transaction txn)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Transactions.Add(txn);
        await db.SaveChangesAsync();
        await BalanceCalculator.RecalculateAsync(db, txn.AccountId);
        return txn;
    }

    public async Task UpdateTransactionAsync(Transaction edited)
    {
        await using var db = await factory.CreateDbContextAsync();
        var txn = await db.Transactions.FirstAsync(t => t.Id == edited.Id);

        // Hand edits to Amount/Date on a synced row must survive the next sync.
        if (txn.Source == TransactionSource.SimpleFin &&
            (txn.Amount != edited.Amount || txn.PostedDate != edited.PostedDate))
        {
            txn.IsUserModified = true;
        }

        txn.Amount = edited.Amount;
        txn.PostedDate = edited.PostedDate;
        txn.Payee = edited.Payee;
        txn.Memo = edited.Memo;
        txn.CheckNumber = edited.CheckNumber;
        txn.CategoryId = edited.CategoryId;
        txn.ClearedStatus = edited.ClearedStatus;
        await db.SaveChangesAsync();
        await BalanceCalculator.RecalculateAsync(db, txn.AccountId);
    }

    // Everything still needing a category, newest first, optionally for one account.
    //
    // Its own query rather than a register filter because the question spans accounts: the point
    // is "what is left to file", and a per-account view answers a different one.
    public async Task<List<Transaction>> GetUncategorizedAsync(int? accountId = null, int limit = 500)
    {
        await using var db = await factory.CreateDbContextAsync();
        var query = db.Transactions.AsNoTracking()
            .Include(t => t.Account)
            .Where(t => t.CategoryId == null);
        if (accountId is { } id)
            query = query.Where(t => t.AccountId == id);

        return await query
            .OrderByDescending(t => t.PostedDate).ThenByDescending(t => t.Id)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> CountUncategorizedAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Transactions.CountAsync(t => t.CategoryId == null);
    }

    // Category only. Deliberately not routed through UpdateTransactionAsync: a category can't
    // move a balance, so there is nothing for BalanceCalculator to redo, and it must not set
    // IsUserModified either — that flag guards Amount/PostedDate against re-sync, and categories
    // are already safe because the sync's update path never writes CategoryId.
    public async Task SetCategoryAsync(int transactionId, int? categoryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var txn = await db.Transactions.FirstOrDefaultAsync(t => t.Id == transactionId);
        if (txn is null)
            return;
        txn.CategoryId = categoryId;
        await db.SaveChangesAsync();
    }

    public async Task DeleteTransactionAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var txn = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id);
        if (txn is null)
            return;
        db.Transactions.Remove(txn);
        await db.SaveChangesAsync();
        await BalanceCalculator.RecalculateAsync(db, txn.AccountId);
    }
}
