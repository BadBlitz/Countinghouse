using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.SimpleFin;

public sealed record AccountSyncResult
{
    public required int AccountId { get; init; }
    public required string AccountName { get; init; }
    public int Inserted { get; init; }
    public int Updated { get; init; }
    public int SkippedBeforeCutoff { get; init; }
    public int ProtectedByUserEdit { get; init; }

    // Newly-inserted rows a category rule filled in. Reported so the rules are visibly doing
    // something; a rule that never fires should look like dead weight, not like success.
    public int AutoCategorized { get; init; }
    public decimal Balance { get; init; }
    public DateOnly? Cutoff { get; init; }

    // True when this account syncs a balance only. Reported rather than silent: an account
    // showing no new transactions for a benign reason should not look like one that is broken.
    public bool BalanceOnly { get; init; }

    // Synced rows removed because the account is balance-only. Normally zero — it is non-zero
    // once, on the first sync after an account becomes balance-only.
    public int RemovedStaleRows { get; init; }

    // Set when local history stops before the oldest day SimpleFIN will still serve. Only a fresh
    // QIF export from Quicken can fill this; syncing again never will.
    public DateOnly? UnreachableFrom { get; init; }
    public DateOnly? UnreachableTo { get; init; }

    public bool HasUnreachableGap => UnreachableFrom is not null;
}

public sealed record ConnectionSyncResult
{
    public required int ConnectionId { get; init; }
    public required string InstitutionLabel { get; init; }
    public List<AccountSyncResult> Accounts { get; init; } = [];

    // Per-account failures the server reported on an otherwise-successful response.
    public List<string> RemoteErrors { get; init; } = [];

    // Remote accounts that aren't mapped to anything local. Never auto-created: which local
    // account a remote one belongs to is a decision only the user can make, and guessing it wrong
    // writes transactions into the wrong register.
    public List<string> UnmappedRemoteAccounts { get; init; } = [];

    public string? FailureMessage { get; init; }
    public bool RequiresRelink { get; init; }
    public bool Succeeded => FailureMessage is null;
}

public sealed class SimpleFinSyncService(
    IDbContextFactory<CountinghouseContext> factory,
    SimpleFinClient client,
    IAccessUrlProtector protector)
{
    public async Task<ConnectionSyncResult> SyncConnectionAsync(
        int connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var connection = await db.SimpleFinConnections.FirstOrDefaultAsync(
            c => c.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return new ConnectionSyncResult
            {
                ConnectionId = connectionId,
                InstitutionLabel = "(unknown)",
                FailureMessage = "That connection no longer exists.",
            };
        }

        if (!protector.TryUnprotect(connection.EncryptedAccessUrl, out var accessUrl))
        {
            return await RecordFailureAsync(db, connection,
                "The saved credentials for this institution can't be read — this usually means the " +
                "database was restored onto a different Windows account. Re-link the institution to fix it.",
                requiresRelink: true, cancellationToken);
        }

        var today = SyncCutoffCalculator.ServerToday(DateTimeOffset.UtcNow);
        var cutoffs = await SyncCutoffCalculator.ForConnectionAsync(db, connectionId, cancellationToken);
        var startDate = SyncCutoffCalculator.EarliestRequestDate(cutoffs, today);

        SimpleFinAccountSet remote;
        try
        {
            remote = await client.GetAccountsAsync(
                accessUrl, startDate, includePending: true, balancesOnly: false, cancellationToken);
        }
        catch (SimpleFinException ex)
        {
            return await RecordFailureAsync(db, connection, ex.Message, ex.RequiresRelink, cancellationToken);
        }

        var localAccounts = await db.Accounts
            .Where(a => a.SimpleFinConnectionId == connectionId)
            .ToListAsync(cancellationToken);

        // Loaded once for the whole run. SimpleFIN carries no category of its own, so without
        // this every synced row lands uncategorized and the spending report thins out over time.
        var rules = await db.CategoryRules.AsNoTracking()
            .Where(r => r.IsEnabled)
            .ToListAsync(cancellationToken);

        var results = new List<AccountSyncResult>();
        var unmapped = new List<string>();

        foreach (var remoteAccount in remote.Accounts)
        {
            var account = localAccounts.FirstOrDefault(a => a.SimpleFinAccountId == remoteAccount.Id);
            if (account is null)
            {
                unmapped.Add($"{remoteAccount.Name} ({remoteAccount.Org?.DisplayName ?? "unknown institution"})");
                continue;
            }

            var cutoff = cutoffs.FirstOrDefault(c => c.AccountId == account.Id)
                         ?? new AccountCutoff(account.Id, remoteAccount.Id, null, null);

            results.Add(await SyncAccountAsync(db, account, remoteAccount, cutoff, rules, today, cancellationToken));
        }

        AttributeErrors(localAccounts, remote);

        connection.LastSyncAt = DateTime.Now;
        connection.LastSyncStatus = remote.Errors.Count > 0 ? "PartialSuccess" : "Success";
        connection.LastSyncErrorMessage = remote.Errors.Count > 0 ? string.Join("; ", remote.Errors) : null;
        await db.SaveChangesAsync(cancellationToken);

        return new ConnectionSyncResult
        {
            ConnectionId = connectionId,
            InstitutionLabel = connection.InstitutionLabel,
            Accounts = results,
            RemoteErrors = remote.Errors,
            UnmappedRemoteAccounts = unmapped,
        };
    }

    private static async Task<AccountSyncResult> SyncAccountAsync(
        CountinghouseContext db,
        Account account,
        SimpleFinAccount remoteAccount,
        AccountCutoff cutoff,
        IReadOnlyList<CategoryRule> rules,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        if (!account.SyncsTransactions)
            return await SyncBalanceOnlyAsync(db, account, remoteAccount, cancellationToken);

        var externalIds = remoteAccount.Transactions.Select(t => t.Id).ToList();
        var existing = await db.Transactions
            .Where(t => t.AccountId == account.Id && t.ExternalId != null && externalIds.Contains(t.ExternalId))
            .ToDictionaryAsync(t => t.ExternalId!, cancellationToken);

        int inserted = 0, updated = 0, skipped = 0, protectedCount = 0;
        var appliedRuleIds = new List<int>();

        foreach (var remoteTransaction in remoteAccount.Transactions)
        {
            // The whole point of the per-account cutoff: this row is already in the register from
            // the QIF import, and QIF rows have no ExternalId to dedupe against.
            if (cutoff.Discards(remoteTransaction.PostedDate))
            {
                skipped++;
                continue;
            }

            if (existing.TryGetValue(remoteTransaction.Id, out var row))
            {
                if (row.IsUserModified)
                {
                    // A hand-corrected amount or date must survive re-sync. Pending is still
                    // tracked because it's the bank's state, not the user's.
                    protectedCount++;
                    row.Pending = remoteTransaction.Pending;
                    continue;
                }

                row.Amount = remoteTransaction.Amount;
                row.PostedDate = remoteTransaction.PostedDate;
                row.Payee = remoteTransaction.BestPayee;
                row.Memo = remoteTransaction.Memo;
                row.Pending = remoteTransaction.Pending;
                // Never walk a reconciled row back to merely cleared — that would silently
                // reopen a closed statement.
                if (row.ClearedStatus != ClearedStatus.Reconciled)
                    row.ClearedStatus = ClearedFor(remoteTransaction);
                updated++;
                continue;
            }

            // Applied to new rows only. An existing row keeps whatever category it has, because
            // a rule overwriting a hand-picked category is indistinguishable from the edit never
            // having saved.
            var matched = CategoryRuleService.FirstMatch(rules, remoteTransaction.BestPayee);
            if (matched is not null)
                appliedRuleIds.Add(matched.Id);

            db.Transactions.Add(new Transaction
            {
                AccountId = account.Id,
                Amount = remoteTransaction.Amount,
                PostedDate = remoteTransaction.PostedDate,
                Payee = remoteTransaction.BestPayee,
                Memo = remoteTransaction.Memo,
                Pending = remoteTransaction.Pending,
                ClearedStatus = ClearedFor(remoteTransaction),
                Source = TransactionSource.SimpleFin,
                ExternalId = remoteTransaction.Id,
                ImportBatchId = null,
                CategoryId = matched?.CategoryId,
            });
            inserted++;
        }

        // Balances for synced accounts come straight from the API and are never derived from the
        // register — the lookback window isn't guaranteed to cover full history, so summing
        // transactions here would produce a confidently wrong number. BalanceCalculator no-ops
        // for these accounts by design.
        account.CurrentBalance = remoteAccount.Balance;
        account.BalanceAsOf = remoteAccount.BalanceAsOf ?? DateTime.Now;

        foreach (var group in appliedRuleIds.GroupBy(id => id))
        {
            var tracked = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == group.Key, cancellationToken);
            if (tracked is not null)
                tracked.TimesApplied += group.Count();
        }

        await db.SaveChangesAsync(cancellationToken);
        await BalanceSnapshotWriter.RecordAsync(db, account, cancellationToken);

        // Balance-only accounts never reach here, so a gap is always meaningful when reported.
        var gap = cutoff.UnreachableGap(today);

        return new AccountSyncResult
        {
            AccountId = account.Id,
            AccountName = account.Name,
            Inserted = inserted,
            Updated = updated,
            AutoCategorized = appliedRuleIds.Count,
            SkippedBeforeCutoff = skipped,
            ProtectedByUserEdit = protectedCount,
            Balance = account.CurrentBalance,
            Cutoff = cutoff.LastImportedDate,
            UnreachableFrom = gap?.From,
            UnreachableTo = gap?.To,
        };
    }

    // Takes the balance and discards any transactions the account picked up before it became
    // balance-only. Only SimpleFIN-sourced rows are removed: hand-entered corrections and QIF
    // history are the user's, not the sync's, and must survive.
    private static async Task<AccountSyncResult> SyncBalanceOnlyAsync(
        CountinghouseContext db,
        Account account,
        SimpleFinAccount remoteAccount,
        CancellationToken cancellationToken)
    {
        var removed = await db.Transactions
            .Where(t => t.AccountId == account.Id && t.Source == TransactionSource.SimpleFin)
            .ExecuteDeleteAsync(cancellationToken);

        account.CurrentBalance = remoteAccount.Balance;
        account.BalanceAsOf = remoteAccount.BalanceAsOf ?? DateTime.Now;
        await db.SaveChangesAsync(cancellationToken);

        // Snapshots matter most here. These accounts hold no register at all, so the snapshot
        // series is the only history they will ever have — and they're the ones carrying most of
        // the net worth.
        await BalanceSnapshotWriter.RecordAsync(db, account, cancellationToken);

        return new AccountSyncResult
        {
            AccountId = account.Id,
            AccountName = account.Name,
            Balance = account.CurrentBalance,
            BalanceOnly = true,
            RemovedStaleRows = removed,
        };
    }

    // Pins each reported error on the accounts it is about. SimpleFIN's errors are free text that
    // names an institution, never an account, so the match is on the institution name the same
    // response gives each account.
    //
    // Anything that can't be pinned lands on every account. That is the old behaviour and the safe
    // direction: an error blamed too widely is noise, one blamed on nobody is a dead feed that
    // looks live. An account the response left out altogether is treated the same way, since an
    // institution that is failing may not be returned at all.
    private static void AttributeErrors(List<Account> localAccounts, SimpleFinAccountSet remote)
    {
        var institutions = remote.Accounts
            .Select(a => a.Org?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();

        var unattributed = remote.Errors
            .Where(e => !institutions.Any(name => e.Contains(name!, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var account in localAccounts)
        {
            var remoteAccount = remote.Accounts.FirstOrDefault(a => a.Id == account.SimpleFinAccountId);
            var institution = remoteAccount?.Org?.Name;

            var own = remoteAccount is null
                ? remote.Errors
                : remote.Errors
                    .Where(e => unattributed.Contains(e)
                                || (!string.IsNullOrWhiteSpace(institution)
                                    && e.Contains(institution, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

            account.LastSyncError = own.Count > 0 ? string.Join("; ", own) : null;
        }
    }

    // A posted transaction has cleared the bank; a pending one hasn't. This mirrors how Quicken
    // treats downloaded rows, and leaves Reconciled purely to the reconciliation workflow.
    private static ClearedStatus ClearedFor(SimpleFinTransaction transaction) =>
        transaction.Pending ? ClearedStatus.Uncleared : ClearedStatus.Cleared;

    private static async Task<ConnectionSyncResult> RecordFailureAsync(
        CountinghouseContext db,
        SimpleFinConnection connection,
        string message,
        bool requiresRelink,
        CancellationToken cancellationToken)
    {
        connection.LastSyncAt = DateTime.Now;
        connection.LastSyncStatus = requiresRelink ? "NeedsRelink" : "Failed";
        connection.LastSyncErrorMessage = message;
        await db.SaveChangesAsync(cancellationToken);

        return new ConnectionSyncResult
        {
            ConnectionId = connection.Id,
            InstitutionLabel = connection.InstitutionLabel,
            FailureMessage = message,
            RequiresRelink = requiresRelink,
        };
    }
}
