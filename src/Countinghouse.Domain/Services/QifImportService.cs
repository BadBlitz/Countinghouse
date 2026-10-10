using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Qif;

namespace Countinghouse.Domain.Services;

// Eagerly creating every category from the file's !Type:Cat block gets correct Income/Expense
// types onto categories that have no transactions in this particular account — worth having,
// because the category list is global across all accounts. Referenced-only keeps the picker
// short. Default is eager.
public sealed record QifImportOptions(bool CreateAllCategoriesFromFile = true);

public sealed record QifImportPreview
{
    public int TransactionCount { get; init; }
    public DateOnly? FirstDate { get; init; }
    public DateOnly? LastDate { get; init; }
    public decimal AmountTotal { get; init; }
    public decimal ProjectedBalance { get; init; }
    public int UnclearedCount { get; init; }
    public int ClearedCount { get; init; }
    public int ReconciledCount { get; init; }
    public int UncategorizedCount { get; init; }
    public int TransferCount { get; init; }
    public int SplitTransactionCount { get; init; }
    public int SkippedSplitLines { get; init; }
    public int DominantSplitCategoryCount { get; init; }
    public int CategoriesInFile { get; init; }
    public int NewCategoriesFromFile { get; init; }
    public int NewCategoriesReferenced { get; init; }
    public int ExistingTransactionCount { get; init; }
    public int ExistingImportedCount { get; init; }
    public List<string> Warnings { get; init; } = [];
    public List<string> AccountHeaders { get; init; } = [];
    public List<QifTransactionRecord> FirstRows { get; init; } = [];
    public List<QifTransactionRecord> LastRows { get; init; } = [];
}

public sealed record QifImportResult(
    Guid BatchId, int Inserted, int CategoriesCreated, decimal NewBalance);

public sealed record QifImportSummary(int ImportedRowCount, int BatchCount, int ManualRowCount);

public class QifImportService(IDbContextFactory<CountinghouseContext> factory)
{
    private const int SampleSize = 8;

    public async Task<(QifImportPreview? Preview, string? Error)> PreviewAsync(int accountId, string qifText)
    {
        await using var db = await factory.CreateDbContextAsync();
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is null)
            return (null, "Account not found.");

        var parsed = QifParser.Parse(qifText);
        if (Validate(parsed) is { } error)
            return (null, error);

        var existing = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .Select(t => new { t.Amount, t.ImportBatchId })
            .ToListAsync();

        var warnings = new List<string>(parsed.Warnings);
        if (account.SimpleFinConnectionId is not null)
        {
            warnings.Insert(0,
                "This account is linked to SimpleFIN. Importing history that overlaps the sync's " +
                "date range creates duplicate transactions — nothing in the app prevents that.");
        }
        if (existing.Count > 0)
        {
            warnings.Insert(0,
                $"This account already has {existing.Count:N0} transaction(s). QIF rows carry no stable id, " +
                "so importing the same file twice duplicates every row — clear the previous import first.");
        }

        var records = parsed.Transactions;
        var total = records.Sum(t => t.Amount);
        var (fromFile, referenced) = await CountNewCategoriesAsync(db, parsed);

        var preview = new QifImportPreview
        {
            TransactionCount = records.Count,
            FirstDate = records.Count == 0 ? null : records.Min(t => t.PostedDate),
            LastDate = records.Count == 0 ? null : records.Max(t => t.PostedDate),
            AmountTotal = total,
            ProjectedBalance = account.OpeningBalance + existing.Sum(t => t.Amount) + total,
            UnclearedCount = records.Count(t => t.ClearedStatus == ClearedStatus.Uncleared),
            ClearedCount = records.Count(t => t.ClearedStatus == ClearedStatus.Cleared),
            ReconciledCount = records.Count(t => t.ClearedStatus == ClearedStatus.Reconciled),
            UncategorizedCount = records.Count(t => t.Category is null),
            TransferCount = records.Count(t => t.Category?.IsTransfer == true),
            SplitTransactionCount = parsed.SplitTransactionCount,
            SkippedSplitLines = parsed.SkippedSplitLines,
            DominantSplitCategoryCount = records.Count(t => t.CategoryFromDominantSplit),
            CategoriesInFile = parsed.Categories.Count,
            NewCategoriesFromFile = fromFile,
            NewCategoriesReferenced = referenced,
            ExistingTransactionCount = existing.Count,
            ExistingImportedCount = existing.Count(t => t.ImportBatchId is not null),
            Warnings = warnings,
            AccountHeaders = parsed.AccountHeaders,
            FirstRows = records.OrderBy(t => t.PostedDate).ThenBy(t => t.SourceLineNumber)
                .Take(SampleSize).ToList(),
            LastRows = records.OrderByDescending(t => t.PostedDate).ThenByDescending(t => t.SourceLineNumber)
                .Take(SampleSize).Reverse().ToList(),
        };
        return (preview, null);
    }

    public async Task<(QifImportResult? Result, string? Error)> ImportAsync(
        int accountId, string qifText, QifImportOptions? options = null)
    {
        options ??= new QifImportOptions();

        await using var db = await factory.CreateDbContextAsync();
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is null)
            return (null, "Account not found.");

        var parsed = QifParser.Parse(qifText);
        if (Validate(parsed) is { } error)
            return (null, error);

        var resolver = await CategoryResolver.CreateAsync(db, parsed.Categories);
        var references = new List<QifCategoryRef>();
        if (options.CreateAllCategoriesFromFile)
            references.AddRange(parsed.Categories.Select(c => new QifCategoryRef(c.Path, IsTransfer: false)));
        references.AddRange(parsed.Transactions.Select(t => t.Category).OfType<QifCategoryRef>());
        await resolver.PrepareAsync(db, references);

        var batchId = Guid.NewGuid();
        var rows = new List<Transaction>(parsed.Transactions.Count);
        foreach (var record in parsed.Transactions)
        {
            rows.Add(new Transaction
            {
                AccountId = accountId,
                Amount = record.Amount,
                PostedDate = record.PostedDate,
                Payee = record.Payee,
                Memo = record.Memo,
                CheckNumber = record.CheckNumber,
                CategoryId = resolver.Resolve(record.Category),
                ClearedStatus = record.ClearedStatus,
                // QIF rows are indistinguishable from hand-entered ones by design; the batch id
                // is the only marker, and it's what makes clear-and-re-import possible.
                Source = TransactionSource.Manual,
                ExternalId = null,
                ImportBatchId = batchId,
            });
        }

        // Ten thousand rows through the change tracker is slow with detection on.
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.Transactions.AddRange(rows);
        db.ChangeTracker.AutoDetectChangesEnabled = true;
        await db.SaveChangesAsync();

        await BalanceCalculator.RecalculateAsync(db, accountId);
        var newBalance = (await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == accountId)).CurrentBalance;

        return (new QifImportResult(batchId, rows.Count, resolver.CreatedCount, newBalance), null);
    }

    public async Task<QifImportSummary> GetImportSummaryAsync(int accountId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var batchIds = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .Select(t => t.ImportBatchId)
            .ToListAsync();

        return new QifImportSummary(
            ImportedRowCount: batchIds.Count(id => id is not null),
            BatchCount: batchIds.Where(id => id is not null).Distinct().Count(),
            ManualRowCount: batchIds.Count(id => id is null));
    }

    // Removes every QIF-imported row for one account, leaving hand-entered transactions and
    // balance adjustments alone. The runbook's alternative is restoring a .db backup by hand.
    public async Task<int> ClearImportedAsync(int accountId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var deleted = await db.Transactions
            .Where(t => t.AccountId == accountId && t.ImportBatchId != null)
            .ExecuteDeleteAsync();
        await BalanceCalculator.RecalculateAsync(db, accountId);
        return deleted;
    }

    private static string? Validate(QifParseResult parsed)
    {
        if (parsed.Transactions.Count > 0)
            return null;

        if (parsed.SkippedInvestmentRecords > 0)
        {
            return $"This is an investment export (!Type:Invst, {parsed.SkippedInvestmentRecords:N0} records). " +
                   "Investment history isn't imported — those accounts track a balance snapshot instead.";
        }
        if (parsed.AccountHeaders.Count == 0)
            return "No !Type:Bank or !Type:CCard transaction section was found in this file.";
        return "The file's transaction section is empty — no transactions to import.";
    }

    private static async Task<(int FromFile, int Referenced)> CountNewCategoriesAsync(
        CountinghouseContext db, QifParseResult parsed)
    {
        var existing = await db.Categories.AsNoTracking()
            .Select(c => new { c.Name, c.ParentCategoryId })
            .ToListAsync();
        var topLevel = existing
            .Where(c => c.ParentCategoryId is null)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Counted as distinct paths rather than resolved ids — close enough for a preview,
        // and it avoids writing anything.
        var fromFile = parsed.Categories
            .Select(c => c.Path)
            .Concat(parsed.Categories.Select(c => c.Path.Split(':')[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(path => !topLevel.Contains(path.Split(':')[0]) || path.Contains(':'));

        var referenced = parsed.Transactions
            .Where(t => t.Category is not null)
            .Select(t => t.Category!.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(path => !topLevel.Contains(path.Split(':')[0]) || path.Contains(':'));

        return (fromFile, referenced);
    }

    // Maps QIF category paths onto Category rows, creating what's missing. One level of
    // nesting only: "Auto:Fuel:Diesel" becomes parent "Auto" / child "Fuel:Diesel".
    //
    // Every category a run needs is created up front in two batched passes (parents, then
    // children) so the per-transaction path is a dictionary lookup — a large import can't
    // afford a round trip per row, and one SaveChanges per category is one transaction each.
    private sealed class CategoryResolver
    {
        private readonly Dictionary<(int? Parent, string Name), int> _byKey = new(KeyComparer.Instance);
        private readonly Dictionary<string, CategoryType> _typesFromFile;

        public int CreatedCount { get; private set; }

        private CategoryResolver(
            IEnumerable<Category> existing, List<QifCategoryDefinition> definitions)
        {
            foreach (var category in existing)
                _byKey[(category.ParentCategoryId, category.Name)] = category.Id;

            _typesFromFile = new Dictionary<string, CategoryType>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in definitions)
                _typesFromFile[definition.Path] = definition.CategoryType;
        }

        public static async Task<CategoryResolver> CreateAsync(
            CountinghouseContext db, List<QifCategoryDefinition> definitions)
        {
            var existing = await db.Categories.ToListAsync();
            return new CategoryResolver(existing, definitions);
        }

        public async Task PrepareAsync(CountinghouseContext db, List<QifCategoryRef> references)
        {
            // Pass 1 — top-level categories. A bracketed transfer reference is always
            // top-level; Transfer type keeps it out of spending reports without auto-linking
            // the two sides of the transfer.
            var pending = new Dictionary<(int? Parent, string Name), Category>(KeyComparer.Instance);
            foreach (var reference in references)
            {
                var (parentName, _) = reference.Split();
                var type = reference.IsTransfer
                    ? CategoryType.Transfer
                    : TypeFor(parentName, reference.Path);
                StagePending(db, pending, null, parentName, type);
            }
            await FlushAsync(db, pending);

            // Pass 2 — children, now that every parent has an id.
            foreach (var reference in references)
            {
                var (parentName, childName) = reference.Split();
                if (childName is null)
                    continue;
                if (!_byKey.TryGetValue((null, parentName), out var parentId))
                    continue;
                StagePending(db, pending, parentId, childName, TypeFor(reference.Path, reference.Path));
            }
            await FlushAsync(db, pending);
        }

        public int? Resolve(QifCategoryRef? reference)
        {
            if (reference is null)
                return null;

            var (parentName, childName) = reference.Split();
            if (!_byKey.TryGetValue((null, parentName), out var parentId))
                return null;
            if (childName is null)
                return parentId;
            return _byKey.TryGetValue((parentId, childName), out var childId) ? childId : parentId;
        }

        // The !Type:Cat block is the only reliable source of Income vs Expense. A parent with
        // no definition of its own inherits the type of the path that referenced it.
        private CategoryType TypeFor(string path, string fallbackPath)
        {
            if (_typesFromFile.TryGetValue(path, out var type))
                return type;
            if (_typesFromFile.TryGetValue(fallbackPath, out type))
                return type;
            return CategoryType.Expense;
        }

        private void StagePending(
            CountinghouseContext db,
            Dictionary<(int? Parent, string Name), Category> pending,
            int? parentId,
            string name,
            CategoryType type)
        {
            var key = (parentId, name);
            if (_byKey.ContainsKey(key) || pending.ContainsKey(key))
                return;
            var category = new Category { Name = name, CategoryType = type, ParentCategoryId = parentId };
            pending[key] = category;
            db.Categories.Add(category);
        }

        private async Task FlushAsync(
            CountinghouseContext db, Dictionary<(int? Parent, string Name), Category> pending)
        {
            if (pending.Count == 0)
                return;

            await db.SaveChangesAsync();
            foreach (var (key, category) in pending)
            {
                _byKey[key] = category.Id;
                CreatedCount++;
            }
            pending.Clear();
        }

        private sealed class KeyComparer : IEqualityComparer<(int? Parent, string Name)>
        {
            public static readonly KeyComparer Instance = new();

            public bool Equals((int? Parent, string Name) x, (int? Parent, string Name) y) =>
                x.Parent == y.Parent && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((int? Parent, string Name) obj) =>
                HashCode.Combine(obj.Parent, obj.Name.ToLowerInvariant());
        }
    }
}
