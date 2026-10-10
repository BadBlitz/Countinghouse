using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Qif;

// A category reference from a transaction's L field, after the /tag suffix is stripped.
// Bracketed values ([Some Account]) are Quicken transfer references — v1 keeps them as
// plain Transfer categories by name rather than auto-linking the two sides.
public sealed record QifCategoryRef(string Path, bool IsTransfer)
{
    // "Auto:Fuel" -> ("Auto", "Fuel"). More than one colon collapses into the child name,
    // since the schema supports exactly one level of nesting.
    public (string Parent, string? Child) Split()
    {
        if (IsTransfer)
            return (Path, null);
        var index = Path.IndexOf(':');
        if (index < 0)
            return (Path, null);
        var parent = Path[..index].Trim();
        var child = Path[(index + 1)..].Trim();
        return child.Length == 0 ? (parent, null) : (parent, child);
    }
}

// One category record from the !Type:Cat block — the only reliable source of Income vs
// Expense, since a transaction's L field carries the name alone.
public sealed record QifCategoryDefinition(string Path, CategoryType CategoryType);

// One flattened transaction. Split (S/E/$) lines are discarded; SplitLineCount records how
// many were dropped so the import preview can report it.
public sealed record QifTransactionRecord
{
    public DateOnly PostedDate { get; init; }
    public decimal Amount { get; init; }
    public string Payee { get; init; } = "";
    public string? Memo { get; init; }
    public string? CheckNumber { get; init; }
    public QifCategoryRef? Category { get; init; }
    public ClearedStatus ClearedStatus { get; init; }
    public int SplitLineCount { get; init; }
    public int SourceLineNumber { get; init; }

    // True when this row's L field was Quicken's "--Split--" marker and the category was taken
    // from the largest split line instead. See QifParser for why.
    public bool CategoryFromDominantSplit { get; init; }

    public bool IsSplit => SplitLineCount > 0;
}

public sealed class QifParseResult
{
    public List<QifCategoryDefinition> Categories { get; } = [];
    public List<QifTransactionRecord> Transactions { get; } = [];

    // Problems worth surfacing before committing an import — unparseable rows, unknown
    // sections, records dropped for missing required fields.
    public List<string> Warnings { get; } = [];

    // The transaction-bearing !Type: headers seen, verbatim and trimmed (e.g. "!Type:Bank").
    public List<string> AccountHeaders { get; } = [];

    // !Type:Invst records seen. Investment history is deliberately out of scope for v1
    // (share movements don't sum to a portfolio value), so these are counted, not imported.
    public int SkippedInvestmentRecords { get; set; }

    public int SkippedSplitLines => Transactions.Sum(t => t.SplitLineCount);
    public int SplitTransactionCount => Transactions.Count(t => t.SplitLineCount > 0);
}
