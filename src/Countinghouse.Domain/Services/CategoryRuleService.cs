using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Services;

// A rule the history supports but that doesn't exist yet. Share and counts are shown rather than
// hidden behind a threshold: "Grocerly, 240 rows, 71% Groceries" is a decision the user can make
// and a bare suggestion isn't.
public sealed record RuleSuggestion(
    string Payee,
    int CategoryId,
    string CategoryFullName,
    int HistoricalCount,
    int HistoricalDominantCount,
    int RecentCount,
    int RecentDominantCount,
    int WouldCategorize,
    bool BasedOnRecent)
{
    // How consistently this payee is filed under one category *now*. Measured over the recent
    // window when there's enough of it, because that's what predicts the next transaction.
    public double Confidence => BasedOnRecent
        ? (RecentCount == 0 ? 0 : (double)RecentDominantCount / RecentCount)
        : AllTimeConfidence;

    public double AllTimeConfidence =>
        HistoricalCount == 0 ? 0 : (double)HistoricalDominantCount / HistoricalCount;

    // The signal that a rule is a bad idea, and invisible if only one figure is shown. A payee
    // can file consistently under one category all-time while its recent filing has drifted to
    // another; the all-time figure alone would recommend a rule that's now often wrong.
    public bool DivergesFromHistory =>
        BasedOnRecent && Math.Abs(Confidence - AllTimeConfidence) >= 0.15;
}

public sealed record RuleApplication(int Matched, int Updated);

public class CategoryRuleService(IDbContextFactory<CountinghouseContext> factory)
{
    // "How you file this payee now". Three years is long enough to cover an annual pattern and
    // short enough that a habit you dropped years ago stops voting.
    public const int RecentWindowMonths = 36;

    // Below this, the recent window is too thin to be evidence and all of history is the better
    // guess — the same reason the UI flags a low history count.
    public const int MinRecentRows = 5;

    public async Task<List<CategoryRule>> GetRulesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var rules = await db.CategoryRules.AsNoTracking()
            .Include(r => r.Category).ThenInclude(c => c!.ParentCategory)
            .ToListAsync();
        return [.. rules.OrderBy(OrderKey).ThenBy(r => r.Id)];
    }

    // Match order, and it is deliberately derived rather than user-managed: exact before contains,
    // then longest pattern first. That makes "Grocerly Fuel" win over "Grocerly" without anyone having
    // to maintain a priority column, which is the only ordering conflict that actually comes up.
    private static (int, int) OrderKey(CategoryRule rule) =>
        ((int)rule.MatchType, -rule.Pattern.Length);

    public async Task<(CategoryRule? Rule, string? Error)> CreateAsync(
        string pattern, CategoryRuleMatch matchType, int categoryId)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
            return (null, "A pattern is required.");

        await using var db = await factory.CreateDbContextAsync();
        if (!await db.Categories.AnyAsync(c => c.Id == categoryId))
            return (null, "That category no longer exists.");

        var duplicate = await db.CategoryRules
            .AnyAsync(r => r.MatchType == matchType && r.Pattern.ToLower() == pattern.ToLower());
        if (duplicate)
            return (null, $"A {matchType.ToString().ToLowerInvariant()} rule for \"{pattern}\" already exists.");

        var rule = new CategoryRule { Pattern = pattern, MatchType = matchType, CategoryId = categoryId };
        db.CategoryRules.Add(rule);
        await db.SaveChangesAsync();
        return (rule, null);
    }

    public async Task<string?> UpdateAsync(int id, string pattern, CategoryRuleMatch matchType, int categoryId, bool isEnabled)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
            return "A pattern is required.";

        await using var db = await factory.CreateDbContextAsync();
        var rule = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null)
            return "That rule no longer exists.";

        rule.Pattern = pattern;
        rule.MatchType = matchType;
        rule.CategoryId = categoryId;
        rule.IsEnabled = isEnabled;
        await db.SaveChangesAsync();
        return null;
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rule = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null)
            return;
        db.CategoryRules.Remove(rule);
        await db.SaveChangesAsync();
    }

    // The category a payee should get, or null. Pure so the sync can call it on rows that don't
    // have ids yet, and so the matching order is testable without a database.
    public static CategoryRule? FirstMatch(IEnumerable<CategoryRule> rules, string payee)
    {
        payee = payee.Trim();
        if (payee.Length == 0)
            return null;

        foreach (var rule in rules.Where(r => r.IsEnabled).OrderBy(OrderKey).ThenBy(r => r.Id))
        {
            var pattern = rule.Pattern.Trim();
            if (pattern.Length == 0)
                continue;

            var hit = rule.MatchType switch
            {
                CategoryRuleMatch.Exact => payee.Equals(pattern, StringComparison.OrdinalIgnoreCase),
                CategoryRuleMatch.Contains => payee.Contains(pattern, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (hit)
                return rule;
        }
        return null;
    }

    public async Task<List<CategoryRule>> GetEnabledRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.CategoryRules.AsNoTracking()
            .Where(r => r.IsEnabled)
            .ToListAsync(cancellationToken);
    }

    // Fills in categories on rows that have none. Never overwrites one that is already set —
    // a category the user chose outranks any rule, and a rule firing over it would be
    // indistinguishable from the edit never having been saved.
    //
    // Takes a caller-supplied DbContext so the sync can run this inside its own unit of work.
    public static async Task<RuleApplication> ApplyAsync(
        CountinghouseContext db,
        IReadOnlyList<CategoryRule> rules,
        int? accountId = null,
        CancellationToken cancellationToken = default)
    {
        if (rules.Count == 0)
            return new RuleApplication(0, 0);

        var query = db.Transactions.Where(t => t.CategoryId == null);
        if (accountId is { } id)
            query = query.Where(t => t.AccountId == id);

        var rows = await query.ToListAsync(cancellationToken);
        var counts = new Dictionary<int, int>();

        foreach (var row in rows)
        {
            if (FirstMatch(rules, row.Payee) is not { } rule)
                continue;
            row.CategoryId = rule.CategoryId;
            counts[rule.Id] = counts.GetValueOrDefault(rule.Id) + 1;
        }

        if (counts.Count > 0)
        {
            var tracked = await db.CategoryRules
                .Where(r => counts.Keys.Contains(r.Id))
                .ToListAsync(cancellationToken);
            foreach (var rule in tracked)
                rule.TimesApplied += counts[rule.Id];
            await db.SaveChangesAsync(cancellationToken);
        }

        // Categories don't affect balances, so no BalanceCalculator pass is needed here.
        return new RuleApplication(rows.Count, counts.Values.Sum());
    }

    public async Task<RuleApplication> ApplyAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rules = await db.CategoryRules.AsNoTracking().Where(r => r.IsEnabled).ToListAsync(cancellationToken);
        return await ApplyAsync(db, rules, accountId: null, cancellationToken);
    }

    // Mines the categorized history for rules worth having. Twenty years of hand-filed
    // transactions is the best training data this app will ever get, and it's already here.
    //
    // Exact-match only: these come from payee strings observed verbatim, and promoting one to a
    // Contains rule is a judgement about a payee's naming that belongs to the user, not to a
    // frequency count.
    public async Task<List<RuleSuggestion>> SuggestFromHistoryAsync(
        DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        var recentCutoff = (today ?? DateOnly.FromDateTime(DateTime.Today)).AddMonths(-RecentWindowMonths);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var existing = await db.CategoryRules.AsNoTracking().ToListAsync(cancellationToken);

        var categorized = await db.Transactions.AsNoTracking()
            .Where(t => t.CategoryId != null && t.Payee != "")
            .Select(t => new { t.Payee, CategoryId = t.CategoryId!.Value, t.PostedDate })
            .ToListAsync(cancellationToken);

        var uncategorized = await db.Transactions.AsNoTracking()
            .Where(t => t.CategoryId == null && t.Payee != "")
            .Select(t => t.Payee)
            .ToListAsync(cancellationToken);

        var categories = await db.Categories.AsNoTracking()
            .Include(c => c.ParentCategory)
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        var pendingByPayee = uncategorized
            .GroupBy(p => p.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var suggestions = new List<RuleSuggestion>();

        foreach (var group in categorized.GroupBy(t => t.Payee.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            // Only worth suggesting if it would actually fix something sitting in the register
            // right now. Ordering by history alone surfaces payees that stopped appearing years ago.
            if (!pendingByPayee.TryGetValue(group.Key, out var wouldFix) || wouldFix == 0)
                continue;

            // Already covered — including by a Contains rule that happens to match, which is the
            // case where re-suggesting would be most annoying.
            if (FirstMatch(existing, group.Key) is not null)
                continue;

            // Recent filing beats long-run filing at predicting the next transaction, and the two
            // genuinely disagree: a payee you used to file one way can drift. Fall back to all of
            // history only when the recent window is too thin to mean anything.
            var recent = group.Where(t => t.PostedDate >= recentCutoff).ToList();
            var basedOnRecent = recent.Count >= MinRecentRows;
            var basis = basedOnRecent ? recent : [.. group];

            var dominant = basis.GroupBy(t => t.CategoryId)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .First();

            if (!categories.TryGetValue(dominant.Key, out var category))
                continue;

            suggestions.Add(new RuleSuggestion(
                group.Key,
                dominant.Key,
                category.FullName,
                group.Count(),
                group.Count(t => t.CategoryId == dominant.Key),
                recent.Count,
                recent.Count(t => t.CategoryId == dominant.Key),
                wouldFix,
                basedOnRecent));
        }

        return [.. suggestions
            .OrderByDescending(s => s.WouldCategorize)
            .ThenByDescending(s => s.Confidence)
            .ThenBy(s => s.Payee)];
    }
}
