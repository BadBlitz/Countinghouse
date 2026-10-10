using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Services;

namespace Countinghouse.SimpleFin;

public sealed record RemoteAccountOption(string Id, string Name, string Institution, decimal Balance);

public sealed record ConnectionSummary(
    int Id,
    string InstitutionLabel,
    int MappedAccountCount,
    DateTime? LastSyncAt,
    string? LastSyncStatus,
    string? LastSyncErrorMessage)
{
    public bool NeedsRelink => LastSyncStatus == "NeedsRelink";
}

// Everything that owns a stored Access URL: claiming one, replacing one, and mapping remote
// accounts onto local ones. Kept apart from SimpleFinSyncService because these are the operations
// that write credentials, and they're the ones worth reading carefully.
public sealed class SimpleFinConnectionService(
    IDbContextFactory<CountinghouseContext> factory,
    SimpleFinClient client,
    IAccessUrlProtector protector)
{
    public async Task<List<ConnectionSummary>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.SimpleFinConnections.AsNoTracking()
            .Select(c => new ConnectionSummary(
                c.Id,
                c.InstitutionLabel,
                c.Accounts.Count,
                c.LastSyncAt,
                c.LastSyncStatus,
                c.LastSyncErrorMessage))
            .ToListAsync(cancellationToken);
    }

    // Claims a setup token and stores the resulting connection.
    //
    // The claim is single-use and irreversible: once the POST succeeds the token is spent, and if
    // we fail to persist the Access URL it is gone for good and the user has to generate another.
    // So the row is written immediately, before any validation, naming, or account mapping — all
    // of which are recoverable, unlike the credential.
    public async Task<(int? ConnectionId, string? Error)> ClaimAsync(
        string institutionLabel, string setupToken, CancellationToken cancellationToken = default)
    {
        string accessUrl;
        try
        {
            accessUrl = await client.ClaimAccessUrlAsync(setupToken, cancellationToken);
        }
        catch (SimpleFinException ex)
        {
            return (null, ex.Message);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var connection = new SimpleFinConnection
        {
            InstitutionLabel = string.IsNullOrWhiteSpace(institutionLabel)
                ? "SimpleFIN connection"
                : institutionLabel.Trim(),
            EncryptedAccessUrl = protector.Protect(accessUrl),
        };
        db.SimpleFinConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);

        return (connection.Id, null);
    }

    // Replaces the credential on an existing connection, keeping its account mappings intact.
    // This is the fix for a key ring that can no longer decrypt the stored URL.
    public async Task<string?> RelinkAsync(
        int connectionId, string setupToken, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var connection = await db.SimpleFinConnections.FirstOrDefaultAsync(
            c => c.Id == connectionId, cancellationToken);
        if (connection is null)
            return "That connection no longer exists.";

        string accessUrl;
        try
        {
            accessUrl = await client.ClaimAccessUrlAsync(setupToken, cancellationToken);
        }
        catch (SimpleFinException ex)
        {
            return ex.Message;
        }

        connection.EncryptedAccessUrl = protector.Protect(accessUrl);
        connection.LastSyncStatus = null;
        connection.LastSyncErrorMessage = null;
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // The remote account list for the mapping screen. Whatever the API returns is what gets
    // shown — no attempt to match it against a planned account list, because the remote names,
    // count, and grouping aren't knowable in advance.
    public async Task<(List<RemoteAccountOption> Accounts, string? Error, bool RequiresRelink)>
        GetRemoteAccountsAsync(int connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var connection = await db.SimpleFinConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
            return ([], "That connection no longer exists.", false);

        if (!protector.TryUnprotect(connection.EncryptedAccessUrl, out var accessUrl))
        {
            return ([],
                "The saved credentials for this institution can't be read. Re-link it to continue.",
                true);
        }

        try
        {
            var set = await client.GetAccountsAsync(
                accessUrl, startDate: null, includePending: false, balancesOnly: true, cancellationToken);

            var options = set.Accounts
                .Select(a => new RemoteAccountOption(
                    a.Id, a.Name, a.Org?.DisplayName ?? "Unknown institution", a.Balance))
                .ToList();

            return (options, set.Errors.Count > 0 ? string.Join("; ", set.Errors) : null, false);
        }
        catch (SimpleFinException ex)
        {
            return ([], ex.Message, ex.RequiresRelink);
        }
    }

    public async Task<string?> MapAccountAsync(
        int accountId, int connectionId, string remoteAccountId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
            return "Account not found.";

        // The (SimpleFinConnectionId, SimpleFinAccountId) unique index would catch this anyway,
        // but a named account is a far more useful message than a constraint violation.
        var takenBy = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(
            a => a.SimpleFinConnectionId == connectionId
                 && a.SimpleFinAccountId == remoteAccountId
                 && a.Id != accountId,
            cancellationToken);
        if (takenBy is not null)
            return $"That remote account is already mapped to \"{takenBy.Name}\".";

        account.SimpleFinConnectionId = connectionId;
        account.SimpleFinAccountId = remoteAccountId;
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // Detaches a local account from its remote counterpart. Transactions already synced are kept:
    // they're real history, and deleting them on a mapping change would be a surprising amount of
    // destruction for what reads like an undo.
    public async Task UnmapAccountAsync(int accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
            return;

        account.SimpleFinConnectionId = null;
        account.SimpleFinAccountId = null;
        await db.SaveChangesAsync(cancellationToken);

        // Now that it's a manual account again, its balance is derived from its register.
        await BalanceCalculator.RecalculateAsync(db, accountId);
    }
}
