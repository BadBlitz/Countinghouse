using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Services;

public class AccountService(IDbContextFactory<CountinghouseContext> factory)
{
    public const string AdjustmentCategoryName = "Balance Adjustment";

    public async Task<List<Account>> GetAccountsAsync(bool includeClosed = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        var query = db.Accounts.AsNoTracking();
        if (!includeClosed)
            query = query.Where(a => !a.IsClosed);
        return await query.OrderBy(a => a.AccountType).ThenBy(a => a.Name).ToListAsync();
    }

    public async Task<Account?> GetAccountAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
    }

    public async Task<Account> CreateAccountAsync(Account account)
    {
        await using var db = await factory.CreateDbContextAsync();
        account.CurrentBalance = account.OpeningBalance;
        account.BalanceAsOf = DateTime.Now;
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    public async Task UpdateAccountAsync(Account edited)
    {
        await using var db = await factory.CreateDbContextAsync();
        var account = await db.Accounts.FirstAsync(a => a.Id == edited.Id);
        account.Name = edited.Name;
        account.AccountType = edited.AccountType;
        account.OpeningBalance = edited.OpeningBalance;
        account.OpeningBalanceDate = edited.OpeningBalanceDate;
        account.IsClosed = edited.IsClosed;
        await db.SaveChangesAsync();
        await BalanceCalculator.RecalculateAsync(db, account.Id);
    }

    // Movement expectations are metadata about how an account is *used*, not about its money, so
    // this deliberately doesn't route through UpdateAccountAsync: there is no balance to
    // recalculate and nothing here can move one.
    public async Task SetMovementExpectationAsync(
        int accountId, MovementExpectation expectation, int? days)
    {
        await using var db = await factory.CreateDbContextAsync();
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        account.ExpectedMovement = expectation;
        account.ExpectedMovementDays = expectation == MovementExpectation.WithinDays ? days : null;
        await db.SaveChangesAsync();
    }

    // Snapshot-update model for manual accounts (e.g. the mortgage): posts an adjusting
    // transaction for the difference rather than tracking amortization line items.
    public async Task UpdateBalanceAsync(int accountId, decimal newBalance, DateOnly asOf)
    {
        await using var db = await factory.CreateDbContextAsync();
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        var difference = newBalance - account.CurrentBalance;
        if (difference != 0m)
        {
            var category = await GetOrCreateAdjustmentCategoryAsync(db);
            db.Transactions.Add(new Transaction
            {
                AccountId = accountId,
                Amount = difference,
                PostedDate = asOf,
                Payee = AdjustmentCategoryName,
                CategoryId = category.Id,
                ClearedStatus = ClearedStatus.Cleared,
                Source = TransactionSource.Manual,
            });
            await db.SaveChangesAsync();
        }
        await BalanceCalculator.RecalculateAsync(db, accountId);
    }

    public async Task<decimal> GetNetWorthAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var balances = await db.Accounts.AsNoTracking()
            .Where(a => !a.IsClosed)
            .Select(a => a.CurrentBalance)
            .ToListAsync();
        return balances.Sum();
    }

    // Also used by ReconciliationService when "Finish Anyway" books a leftover difference.
    internal static async Task<Category> GetOrCreateAdjustmentCategoryAsync(CountinghouseContext db)
    {
        var category = await db.Categories
            .FirstOrDefaultAsync(c => c.ParentCategoryId == null && c.Name == AdjustmentCategoryName);
        if (category is null)
        {
            // Transfer type keeps adjustments out of the spending-by-category report.
            category = new Category { Name = AdjustmentCategoryName, CategoryType = CategoryType.Transfer };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
        }
        return category;
    }
}
