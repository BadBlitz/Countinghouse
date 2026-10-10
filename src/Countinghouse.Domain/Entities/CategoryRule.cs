namespace Countinghouse.Domain.Entities;

public enum CategoryRuleMatch
{
    // The payee equals the pattern, ignoring case and surrounding whitespace. What rules seeded
    // from history use, because they're derived from payee strings that already exist verbatim.
    Exact = 0,

    // The payee contains the pattern. What a hand-written rule usually wants, since a feed
    // decorates the name it sends ("Walletpay Corner Dairy", "GROCERLY #0123").
    Contains = 1,
}

// Payee pattern to category, applied to transactions that arrive with no category of their own.
//
// Every SimpleFIN row lands uncategorized — the protocol carries no category — so without this
// the spending report degrades a little with every sync while the categorized QIF history recedes.
public class CategoryRule
{
    public int Id { get; set; }

    public string Pattern { get; set; } = "";
    public CategoryRuleMatch MatchType { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    public bool IsEnabled { get; set; } = true;

    // Bookkeeping so the rules page can show what a rule has actually done, and so a rule that
    // never fires is visible as dead weight rather than assumed to be working.
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int TimesApplied { get; set; }
}
