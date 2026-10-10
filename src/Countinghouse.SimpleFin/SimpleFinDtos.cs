using System.Text.Json.Serialization;

namespace Countinghouse.SimpleFin;

// The wire shape of GET /accounts, kept deliberately literal: hyphenated protocol names, no
// renaming toward our own entities, no computed money. Mapping onto Account/Transaction happens
// in the sync service, so a protocol change surfaces here as one obviously-wrong DTO rather than
// as a silently-null balance three layers in.
public sealed class SimpleFinAccountSet
{
    // Per-account failures arrive here on an otherwise-200 response. A partial success is
    // byte-for-byte a normal success apart from this list, so ignoring it means a bank that
    // stopped responding looks exactly like a bank with no new transactions.
    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = [];

    [JsonPropertyName("accounts")]
    public List<SimpleFinAccount> Accounts { get; set; } = [];
}

public sealed class SimpleFinOrg
{
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("sfin-url")] public string? SfinUrl { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }

    // Only `sfin-url` is required by the spec; everything else here is optional, so the label
    // shown on the mapping screen has to degrade rather than blank out.
    public string DisplayName =>
        FirstNonBlank(Name, Domain, Id) ?? "Unknown institution";

    internal static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}

public sealed class SimpleFinAccount
{
    [JsonPropertyName("org")] public SimpleFinOrg? Org { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("currency")] public string? Currency { get; set; }

    [JsonPropertyName("balance")]
    [JsonConverter(typeof(SimpleFinDecimalConverter))]
    public decimal Balance { get; set; }

    [JsonPropertyName("available-balance")]
    [JsonConverter(typeof(SimpleFinNullableDecimalConverter))]
    public decimal? AvailableBalance { get; set; }

    [JsonPropertyName("balance-date")] public long? BalanceDate { get; set; }

    [JsonPropertyName("transactions")]
    public List<SimpleFinTransaction> Transactions { get; set; } = [];

    // Local, unlike the transaction dates above. `balance-date` is a real wall-clock instant
    // rather than a normalized midday stamp, and it is rendered to the user as "balance as of",
    // where a UTC time reads as hours in the future.
    public DateTime? BalanceAsOf => BalanceDate is { } epoch
        ? DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime
        : null;
}

public sealed class SimpleFinTransaction
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("posted")] public long Posted { get; set; }

    [JsonPropertyName("amount")]
    [JsonConverter(typeof(SimpleFinDecimalConverter))]
    public decimal Amount { get; set; }

    [JsonPropertyName("description")] public string? Description { get; set; }

    // `payee` and `memo` are Bridge additions, not in the v1 spec — present for some
    // institutions and absent for others, which is why Payee falls back to description.
    [JsonPropertyName("payee")] public string? Payee { get; set; }
    [JsonPropertyName("memo")] public string? Memo { get; set; }

    [JsonPropertyName("transacted_at")] public long? TransactedAt { get; set; }
    [JsonPropertyName("pending")] public bool Pending { get; set; }

    // Which field this uses matters; the timezone it is read in does not.
    //
    // Both timestamps come back normalized to midday UTC, so UTC and local produce the same
    // calendar date. The real distinction is that `posted` is when the transaction hit the account
    // and `transacted_at` is when it happened, and for a credit card those differ: a purchase can
    // transact one day and post the next, while a same-day payment transacts and posts together.
    //
    // The transaction date is what the bank's own UI shows, what a statement reconciles against,
    // and what imported history already in the register uses. Taking `posted` would put every
    // synced row a day later than all three. `transacted_at` is optional in the protocol, so
    // `posted` remains the fallback.
    public DateOnly PostedDate =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(TransactedAt ?? Posted).UtcDateTime);

    // Never blank: the register's Payee column is required, and an empty one makes a row
    // impossible to identify during reconciliation.
    public string BestPayee =>
        SimpleFinOrg.FirstNonBlank(Payee, Description) ?? "(no description)";
}
