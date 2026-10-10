using System.Net;
using System.Text;
using Countinghouse.Domain.Entities;
using Countinghouse.SimpleFin;

namespace Countinghouse.Tests;

public class SimpleFinConnectionServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private const string AccessUrl = "https://user:pass@beta-bridge.simplefin.org/simplefin";

    private sealed class FakeProtector : IAccessUrlProtector
    {
        public string Protect(string accessUrl) => "enc:" + accessUrl;

        public bool TryUnprotect(string encryptedAccessUrl, out string accessUrl)
        {
            accessUrl = encryptedAccessUrl.StartsWith("enc:") ? encryptedAccessUrl[4..] : encryptedAccessUrl;
            return true;
        }
    }

    private SimpleFinConnectionService ServiceFor(HttpStatusCode status, string body) =>
        new(_db, new SimpleFinClient(StubHttpMessageHandler.Client(status, body)), new FakeProtector());

    private static string Token(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl));

    private const string ClaimToken = "aHR0cHM6Ly9iZXRhLWJyaWRnZS5zaW1wbGVmaW4ub3JnL2NsYWltL1g=";

    [Fact]
    public async Task ClaimingStoresTheAccessUrlEncryptedAndNeverInPlaintext()
    {
        var service = ServiceFor(HttpStatusCode.OK, AccessUrl);

        var (connectionId, error) = await service.ClaimAsync("Main Bank", ClaimToken);

        Assert.Null(error);
        Assert.NotNull(connectionId);

        await using var db = _db.CreateDbContext();
        var connection = db.SimpleFinConnections.Single();
        Assert.Equal("Main Bank", connection.InstitutionLabel);
        // Stored via the protector rather than raw. Whether the ciphertext is actually
        // unreadable is DataProtectionAccessUrlProtector's job, not something a fake can show.
        Assert.Equal("enc:" + AccessUrl, connection.EncryptedAccessUrl);
    }

    [Fact]
    public async Task AFailedClaimStoresNothing()
    {
        var service = ServiceFor(HttpStatusCode.Forbidden, "");

        var (connectionId, error) = await service.ClaimAsync("Main Bank", ClaimToken);

        Assert.Null(connectionId);
        Assert.NotNull(error);

        await using var db = _db.CreateDbContext();
        Assert.Empty(db.SimpleFinConnections);
    }

    [Fact]
    public async Task ABlankInstitutionNameStillProducesAUsableLabel()
    {
        var service = ServiceFor(HttpStatusCode.OK, AccessUrl);

        await service.ClaimAsync("   ", ClaimToken);

        await using var db = _db.CreateDbContext();
        Assert.False(string.IsNullOrWhiteSpace(db.SimpleFinConnections.Single().InstitutionLabel));
    }

    [Fact]
    public async Task RelinkingReplacesTheCredentialAndKeepsMappingsAndHistory()
    {
        var service = ServiceFor(HttpStatusCode.OK, AccessUrl);
        var (connectionId, _) = await service.ClaimAsync("Main Bank", ClaimToken);

        int accountId;
        await using (var seed = _db.CreateDbContext())
        {
            var connection = seed.SimpleFinConnections.Single();
            connection.LastSyncStatus = "NeedsRelink";
            connection.LastSyncErrorMessage = "could not decrypt";

            var seeded = new Account
            {
                Name = "Main Bank Checking",
                AccountType = AccountType.Checking,
                SimpleFinConnectionId = connection.Id,
                SimpleFinAccountId = "remote-1",
            };
            seed.Accounts.Add(seeded);
            await seed.SaveChangesAsync();
            accountId = seeded.Id;

            seed.Transactions.Add(new Transaction
            {
                AccountId = accountId, Amount = -5m, Payee = "already synced",
                PostedDate = new DateOnly(2026, 8, 10),
                Source = TransactionSource.SimpleFin, ExternalId = "TXN-1",
            });
            await seed.SaveChangesAsync();
        }

        var newAccessUrl = "https://user2:pass2@beta-bridge.simplefin.org/simplefin";
        var relinkService = ServiceFor(HttpStatusCode.OK, newAccessUrl);
        var error = await relinkService.RelinkAsync(connectionId!.Value, ClaimToken);

        Assert.Null(error);

        await using var db = _db.CreateDbContext();
        var updated = db.SimpleFinConnections.Single();
        Assert.Equal("enc:" + newAccessUrl, updated.EncryptedAccessUrl);
        // The stale failure has to clear, or the connection keeps reading as broken.
        Assert.Null(updated.LastSyncStatus);
        Assert.Null(updated.LastSyncErrorMessage);

        var account = db.Accounts.Single();
        Assert.Equal(connectionId, account.SimpleFinConnectionId);
        Assert.Equal("remote-1", account.SimpleFinAccountId);
        Assert.Single(db.Transactions);
    }

    [Fact]
    public async Task AFailedRelinkLeavesTheOldCredentialInPlace()
    {
        var service = ServiceFor(HttpStatusCode.OK, AccessUrl);
        var (connectionId, _) = await service.ClaimAsync("Main Bank", ClaimToken);

        var error = await ServiceFor(HttpStatusCode.Forbidden, "").RelinkAsync(connectionId!.Value, ClaimToken);

        Assert.NotNull(error);

        await using var db = _db.CreateDbContext();
        // Overwriting with nothing would turn a recoverable problem into a lost connection.
        Assert.Equal("enc:" + AccessUrl, db.SimpleFinConnections.Single().EncryptedAccessUrl);
    }

    [Fact]
    public async Task MappingTheSameRemoteAccountTwiceIsRefusedWithTheConflictingName()
    {
        var service = ServiceFor(HttpStatusCode.OK, AccessUrl);
        var (connectionId, _) = await service.ClaimAsync("Main Bank", ClaimToken);

        int firstId, secondId;
        await using (var seed = _db.CreateDbContext())
        {
            var first = new Account { Name = "Main Bank Checking", AccountType = AccountType.Checking };
            var second = new Account { Name = "Main Bank Savings", AccountType = AccountType.Savings };
            seed.Accounts.AddRange(first, second);
            await seed.SaveChangesAsync();
            (firstId, secondId) = (first.Id, second.Id);
        }

        Assert.Null(await service.MapAccountAsync(firstId, connectionId!.Value, "remote-1"));
        var error = await service.MapAccountAsync(secondId, connectionId.Value, "remote-1");

        Assert.NotNull(error);
        Assert.Contains("Main Bank Checking", error);
    }

    [Fact]
    public async Task UnmappingKeepsSyncedHistoryAndReturnsTheAccountToADerivedBalance()
    {
        var service = ServiceFor(HttpStatusCode.OK, AccessUrl);
        var (connectionId, _) = await service.ClaimAsync("Main Bank", ClaimToken);

        int accountId;
        await using (var seed = _db.CreateDbContext())
        {
            var seeded = new Account
            {
                Name = "Main Bank Checking",
                AccountType = AccountType.Checking,
                OpeningBalance = 100m,
                SimpleFinConnectionId = connectionId,
                SimpleFinAccountId = "remote-1",
                // What the API last said, which is not what the register sums to.
                CurrentBalance = 9999m,
            };
            seed.Accounts.Add(seeded);
            await seed.SaveChangesAsync();
            accountId = seeded.Id;

            seed.Transactions.Add(new Transaction
            {
                AccountId = accountId, Amount = -25m, Payee = "synced row",
                PostedDate = new DateOnly(2026, 8, 10),
                Source = TransactionSource.SimpleFin, ExternalId = "TXN-1",
            });
            await seed.SaveChangesAsync();
        }

        await service.UnmapAccountAsync(accountId);

        await using var db = _db.CreateDbContext();
        var account = db.Accounts.Single();
        Assert.Null(account.SimpleFinConnectionId);
        Assert.Null(account.SimpleFinAccountId);
        // Now a manual account: OpeningBalance + sum(transactions).
        Assert.Equal(75m, account.CurrentBalance);
        // Deleting real history on a mapping change would be a lot of destruction for an undo.
        Assert.Single(db.Transactions);
    }

    [Fact]
    public async Task RemoteAccountsAreListedExactlyAsReturnedWithNoMatchingAgainstLocalNames()
    {
        var json = """
        {
          "errors": [],
          "accounts": [
            { "org": { "name": "Brokerage", "sfin-url": "https://x/simplefin" },
              "id": "fid-1", "name": "Z6543210", "currency": "USD",
              "balance": "12345.67", "transactions": [] }
          ]
        }
        """;
        // The claim and the account listing are two different requests with two different
        // response bodies, so each needs its own stub.
        var (connectionId, claimError) = await ServiceFor(HttpStatusCode.OK, AccessUrl)
            .ClaimAsync("Brokerage", ClaimToken);
        Assert.Null(claimError);

        var (accounts, error, requiresRelink) = await ServiceFor(HttpStatusCode.OK, json)
            .GetRemoteAccountsAsync(connectionId!.Value);

        Assert.Null(error);
        Assert.False(requiresRelink);
        var account = Assert.Single(accounts);
        // An opaque brokerage account number, nothing like "Brokerage IRA" — which is exactly why
        // the mapping screen shows what came back rather than guessing.
        Assert.Equal("Z6543210", account.Name);
        Assert.Equal("fid-1", account.Id);
        Assert.Equal(12345.67m, account.Balance);
    }
}
