using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Services;

// One account in the optional bootstrap list read from AppPaths.AccountPlanPath. Creating a
// dozen accounts through the ordinary form is 48 fields and one silent-failure trap — see
// NeedsBalance — so a plan file is worth having when the inventory is known up front, typically
// straight off a set of QIF exports.
public sealed record PlannedAccount
{
    public required string Name { get; init; }
    public required AccountType AccountType { get; init; }

    // Day before the account's earliest Quicken transaction, for the importing accounts;
    // "today" is substituted for the snapshot accounts when they're created.
    public DateOnly? OpeningBalanceDate { get; init; }

    // True for accounts whose history arrives by QIF import. Their opening balance MUST be 0.00:
    // Quicken exports its own opening-balance row as the first transaction, and CurrentBalance is
    // OpeningBalance + sum(transactions). Entering today's real balance here yields today's
    // balance *plus* all of history, wrong and silent. The setup page offers no balance field on
    // these rows at all, which is what turns a documented trap into an unreachable one.
    public bool ImportsHistory { get; init; }

    // Snapshot accounts need a balance only the user can supply: investment history is share
    // movements, so nothing in the exports can produce a portfolio value.
    public bool NeedsBalance => !ImportsHistory;

    public string Notes { get; init; } = "";
}

// The bootstrap list, read from JSON at AppPaths.AccountPlanPath. Deliberately outside the repo:
// it names real accounts and institutions. A missing file yields an empty plan rather than an
// error — the setup page is an optional convenience, not a required step.
public sealed record AccountPlan(IReadOnlyList<PlannedAccount> Accounts)
{
    public static AccountPlan Empty { get; } = new([]);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AccountPlan Load(string path)
    {
        if (!File.Exists(path))
            return Empty;

        var accounts = JsonSerializer.Deserialize<List<PlannedAccount>>(File.ReadAllText(path), Options);
        return accounts is null or [] ? Empty : new AccountPlan(accounts);
    }

    public static AccountPlan LoadDefault() => Load(AppPaths.AccountPlanPath);
}

public sealed record PlannedAccountStatus(PlannedAccount Planned, int? ExistingAccountId)
{
    public bool Exists => ExistingAccountId is not null;
}

public sealed record AccountSetupResult(int Created, int Skipped);

public class AccountSetupService(IDbContextFactory<CountinghouseContext> factory, AccountPlan plan)
{
    public IReadOnlyList<PlannedAccount> Plan => plan.Accounts;

    // Matched by name so the page is safe to revisit: anything already created is reported as
    // existing and left completely untouched.
    public async Task<List<PlannedAccountStatus>> GetStatusAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Accounts.AsNoTracking()
            .Select(a => new { a.Id, a.Name })
            .ToListAsync();

        return Plan.Select(planned =>
        {
            var match = existing.FirstOrDefault(a =>
                string.Equals(a.Name, planned.Name, StringComparison.OrdinalIgnoreCase));
            return new PlannedAccountStatus(planned, match?.Id);
        }).ToList();
    }

    // Creates only the accounts that don't already exist. snapshotBalances is keyed by account
    // name; a missing or zero entry just means that account starts at 0.00 and gets its real
    // figure later via Edit Account.
    public async Task<AccountSetupResult> CreateMissingAsync(
        IReadOnlyDictionary<string, decimal>? snapshotBalances = null)
    {
        var statuses = await GetStatusAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        await using var db = await factory.CreateDbContextAsync();
        var created = 0;
        foreach (var status in statuses.Where(s => !s.Exists))
        {
            var planned = status.Planned;
            var opening = 0m;
            if (planned.NeedsBalance && snapshotBalances is not null &&
                snapshotBalances.TryGetValue(planned.Name, out var balance))
            {
                opening = balance;
            }

            db.Accounts.Add(new Account
            {
                Name = planned.Name,
                AccountType = planned.AccountType,
                OpeningBalance = opening,
                OpeningBalanceDate = planned.OpeningBalanceDate ?? today,
                // Mirrors AccountService.CreateAccountAsync: with no transactions yet,
                // CurrentBalance is the opening balance.
                CurrentBalance = opening,
                BalanceAsOf = DateTime.Now,
            });
            created++;
        }

        if (created > 0)
            await db.SaveChangesAsync();

        return new AccountSetupResult(created, statuses.Count - created);
    }
}
