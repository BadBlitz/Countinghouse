using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.Tests;

public class AccountServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AccountService _accounts;
    private readonly TransactionService _transactions;

    public AccountServiceTests()
    {
        _accounts = new AccountService(_db);
        _transactions = new TransactionService(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateAccount_SetsCurrentBalanceFromOpeningBalance()
    {
        var account = await _accounts.CreateAccountAsync(new Account
        {
            Name = "Mortgage",
            AccountType = AccountType.Mortgage,
            OpeningBalance = -250000m,
        });

        Assert.Equal(-250000m, account.CurrentBalance);
        Assert.NotNull(account.BalanceAsOf);
    }

    [Fact]
    public async Task UpdateBalance_PostsAdjustmentTransactionForDifference()
    {
        var account = await _accounts.CreateAccountAsync(new Account
        {
            Name = "Mortgage",
            AccountType = AccountType.Mortgage,
            OpeningBalance = -250000m,
        });

        await _accounts.UpdateBalanceAsync(account.Id, -248500m, new DateOnly(2026, 2, 1));

        var reloaded = await _accounts.GetAccountAsync(account.Id);
        Assert.Equal(-248500m, reloaded!.CurrentBalance);

        var register = await _transactions.GetRegisterAsync(account.Id);
        var adjustment = Assert.Single(register).Transaction;
        Assert.Equal(1500m, adjustment.Amount);
        Assert.Equal(AccountService.AdjustmentCategoryName, adjustment.Payee);
        Assert.Equal(AccountService.AdjustmentCategoryName, adjustment.Category?.Name);
    }

    [Fact]
    public async Task UpdateBalance_NoChange_PostsNothing()
    {
        var account = await _accounts.CreateAccountAsync(new Account
        {
            Name = "Checking",
            AccountType = AccountType.Checking,
            OpeningBalance = 500m,
        });

        await _accounts.UpdateBalanceAsync(account.Id, 500m, new DateOnly(2026, 2, 1));

        Assert.Empty(await _transactions.GetRegisterAsync(account.Id));
    }

    [Fact]
    public async Task NetWorth_SumsOpenAccountsOnly()
    {
        await _accounts.CreateAccountAsync(new Account { Name = "A", OpeningBalance = 1000m });
        await _accounts.CreateAccountAsync(new Account { Name = "B", OpeningBalance = -300m });
        var closed = await _accounts.CreateAccountAsync(new Account { Name = "C", OpeningBalance = 999m });
        closed.IsClosed = true;
        await _accounts.UpdateAccountAsync(closed);

        Assert.Equal(700m, await _accounts.GetNetWorthAsync());
    }

    [Fact]
    public async Task SyncedAccountBalance_IsNotRecalculatedFromTransactions()
    {
        int accountId;
        await using (var db = _db.CreateDbContext())
        {
            var connection = new SimpleFinConnection { InstitutionLabel = "Brokerage", EncryptedAccessUrl = "x" };
            db.SimpleFinConnections.Add(connection);
            await db.SaveChangesAsync();
            var account = new Account
            {
                Name = "Brokerage",
                AccountType = AccountType.Investment,
                CurrentBalance = 50000m,
                SimpleFinConnectionId = connection.Id,
                SimpleFinAccountId = "act-1",
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            accountId = account.Id;
        }

        await _transactions.AddTransactionAsync(new Transaction
        {
            AccountId = accountId,
            Amount = -100m,
            PostedDate = new DateOnly(2026, 1, 5),
            Payee = "Test",
        });

        var reloaded = await _accounts.GetAccountAsync(accountId);
        Assert.Equal(50000m, reloaded!.CurrentBalance);
    }
}
