using System.Globalization;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Qif;

// Parser for Quicken QIF exports, written against real exports rather than the formal QIF spec
// (see CLAUDE.md for the quirks this handles). Pure text -> records: no database, no I/O, so
// it's cheap to test against small fixtures.
//
// Scope is !Type:Bank and !Type:CCard only. !Type:Invst records are counted and skipped —
// investment rows are share movements, and summing them yields uninvested cash rather than
// portfolio value.
public static class QifParser
{
    private enum Section
    {
        // Before any recognized header, or inside a header whose records we ignore wholesale.
        Unknown,
        Categories,
        Tags,
        Accounts,
        Transactions,
        Investments,
    }

    // Headers whose records are cash transactions we can import.
    private static readonly string[] TransactionHeaders =
        ["!type:bank", "!type:ccard", "!type:cash", "!type:oth a", "!type:oth l"];

    public static QifParseResult Parse(string text)
    {
        using var reader = new StringReader(text);
        return Parse(reader);
    }

    public static QifParseResult Parse(TextReader reader)
    {
        var result = new QifParseResult();
        var section = Section.Unknown;
        var record = new List<(char Code, string Value)>();
        var recordStartLine = 0;
        var lineNumber = 0;
        var unknownHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (reader.ReadLine() is { } rawLine)
        {
            lineNumber++;
            // Trailing whitespace is real in these files ("!Type:Bank " in one export).
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
                continue;

            if (line[0] == '!')
            {
                // A header ends whatever record was open; Quicken always terminates with ^,
                // but a truncated file shouldn't leak a half-record into the next section.
                record.Clear();
                var header = line.Trim();
                var key = header.ToLowerInvariant();

                if (key.StartsWith("!option:") || key.StartsWith("!clear:"))
                {
                    // Directives, not sections — they carry no records and don't switch context.
                    continue;
                }

                if (key == "!type:cat")
                    section = Section.Categories;
                else if (key == "!type:tag")
                    section = Section.Tags;
                else if (key == "!account")
                    section = Section.Accounts;
                else if (key == "!type:invst")
                    section = Section.Investments;
                else if (TransactionHeaders.Contains(key))
                {
                    section = Section.Transactions;
                    if (!result.AccountHeaders.Contains(header))
                        result.AccountHeaders.Add(header);
                }
                else
                {
                    section = Section.Unknown;
                    unknownHeaders.Add(header);
                }
                continue;
            }

            if (line[0] == '^')
            {
                if (record.Count > 0)
                {
                    CommitRecord(result, section, record, recordStartLine);
                    record.Clear();
                }
                continue;
            }

            if (record.Count == 0)
                recordStartLine = lineNumber;
            record.Add((line[0], line[1..]));
        }

        // A final record with no terminating ^ still counts.
        if (record.Count > 0)
            CommitRecord(result, section, record, recordStartLine);

        foreach (var header in unknownHeaders)
            result.Warnings.Add($"Ignored unrecognized section \"{header}\".");
        if (result.SkippedInvestmentRecords > 0)
            result.Warnings.Add(
                $"Skipped {result.SkippedInvestmentRecords:N0} investment (!Type:Invst) records — " +
                "investment history is out of scope; those accounts use the snapshot model.");

        return result;
    }

    private static void CommitRecord(
        QifParseResult result, Section section, List<(char Code, string Value)> record, int startLine)
    {
        switch (section)
        {
            case Section.Categories:
                CommitCategory(result, record);
                break;
            case Section.Transactions:
                CommitTransaction(result, record, startLine);
                break;
            case Section.Investments:
                result.SkippedInvestmentRecords++;
                break;
            // Tags have no counterpart in the schema; account blocks are metadata we don't need
            // (the target account is chosen in the UI, one file at a time).
            case Section.Tags:
            case Section.Accounts:
            case Section.Unknown:
            default:
                break;
        }
    }

    private static void CommitCategory(QifParseResult result, List<(char Code, string Value)> record)
    {
        string? name = null;
        var isIncome = false;
        var isExpense = false;

        foreach (var (code, value) in record)
        {
            switch (code)
            {
                case 'N':
                    name = value.Trim();
                    break;
                case 'I':
                    isIncome = true;
                    break;
                case 'E':
                    isExpense = true;
                    break;
                // D description, T tax-related flag, R tax schedule, B budget — not modeled.
            }
        }

        if (string.IsNullOrEmpty(name))
            return;

        // E and I are the authority here. Neither flag set is rare; Expense is the safer default
        // since it only affects which side of a spending report a category lands on.
        var type = isIncome && !isExpense ? CategoryType.Income : CategoryType.Expense;
        result.Categories.Add(new QifCategoryDefinition(name, type));
    }

    private static void CommitTransaction(
        QifParseResult result, List<(char Code, string Value)> record, int startLine)
    {
        string? dateRaw = null;
        string? amountRaw = null;
        string? shadowAmountRaw = null;
        string? payee = null;
        string? memo = null;
        string? checkNumber = null;
        string? categoryRaw = null;
        var cleared = ClearedStatus.Uncleared;
        var splitLines = 0;
        var splits = new List<(QifCategoryRef? Category, decimal Amount)>();

        foreach (var (code, value) in record)
        {
            switch (code)
            {
                case 'D':
                    dateRaw = value;
                    break;
                case 'T':
                    amountRaw = value;
                    break;
                case 'U':
                    // Undocumented Quicken field shadowing T on every row. Only a fallback.
                    shadowAmountRaw = value;
                    break;
                case 'P':
                    payee = value.Trim();
                    break;
                case 'M':
                    memo = value.Trim();
                    break;
                case 'N':
                    checkNumber = value.Trim();
                    break;
                case 'L':
                    categoryRaw = value;
                    break;
                case 'C':
                    cleared = ParseClearedStatus(value);
                    break;
                // Split detail lines arrive as repeating (S, E?, $) triplets. Splits are out of
                // scope for v1: the parent D/T/P is authoritative, and the split amounts were
                // verified to sum to the parent exactly, so flattening preserves every balance.
                // The category is the one thing worth salvaging — see below.
                case 'S':
                    splitLines++;
                    splits.Add((ParseCategoryRef(value), 0m));
                    break;
                case 'E':
                    splitLines++;
                    break;
                case '$':
                    splitLines++;
                    if (splits.Count > 0 && TryParseAmount(value, out var splitAmount))
                        splits[^1] = (splits[^1].Category, splitAmount);
                    break;
                // A address lines, F reimbursable flag — not modeled.
            }
        }

        if (dateRaw is null)
        {
            result.Warnings.Add($"Line {startLine}: skipped a record with no date (D) field.");
            return;
        }
        if (!TryParseDate(dateRaw, out var postedDate))
        {
            result.Warnings.Add($"Line {startLine}: skipped a record with an unparseable date \"{dateRaw.Trim()}\".");
            return;
        }

        var rawAmount = amountRaw ?? shadowAmountRaw;
        if (rawAmount is null)
        {
            result.Warnings.Add($"Line {startLine}: skipped a record with no amount (T) field.");
            return;
        }
        if (!TryParseAmount(rawAmount, out var amount))
        {
            result.Warnings.Add($"Line {startLine}: skipped a record with an unparseable amount \"{rawAmount.Trim()}\".");
            return;
        }

        // A split parent's L field is the literal marker "--Split--", not a category. Taken at
        // face value it would create a junk category by that name on every split row. Falling
        // back to the largest split line gets the useful answer instead — on a paycheck that's
        // the gross-pay line, which is the right category for the net deposit anyway. No balance
        // is affected either way.
        var category = ParseCategoryRef(categoryRaw);
        var fromDominantSplit = false;
        if (IsSplitMarker(categoryRaw) || category is null)
        {
            var dominant = splits
                .Where(s => s.Category is not null)
                .OrderByDescending(s => Math.Abs(s.Amount))
                .Select(s => s.Category)
                .FirstOrDefault();
            if (dominant is not null)
            {
                category = dominant;
                fromDominantSplit = true;
            }
            else if (IsSplitMarker(categoryRaw))
            {
                category = null;
            }
        }

        result.Transactions.Add(new QifTransactionRecord
        {
            PostedDate = postedDate,
            Amount = amount,
            // Payee is non-null in the schema. A handful of real rows carry neither P nor M;
            // a placeholder keeps them visible in the register instead of rendering blank.
            Payee = !string.IsNullOrWhiteSpace(payee) ? payee
                : !string.IsNullOrWhiteSpace(memo) ? memo!
                : "(no payee)",
            Memo = string.IsNullOrWhiteSpace(memo) ? null : memo,
            CheckNumber = string.IsNullOrWhiteSpace(checkNumber) ? null : checkNumber,
            Category = category,
            ClearedStatus = cleared,
            SplitLineCount = splitLines,
            SourceLineNumber = startLine,
            CategoryFromDominantSplit = fromDominantSplit,
        });
    }

    private static bool IsSplitMarker(string? raw) =>
        raw is not null && raw.Trim().StartsWith("--Split--", StringComparison.OrdinalIgnoreCase);

    // QIF C field: * or c = cleared, X or R = reconciled, absent/blank = uncleared.
    // Getting this right keeps already-reconciled history out of the first reconciliation.
    internal static ClearedStatus ParseClearedStatus(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return ClearedStatus.Uncleared;
        return char.ToUpperInvariant(trimmed[0]) switch
        {
            '*' or 'C' => ClearedStatus.Cleared,
            'X' or 'R' => ClearedStatus.Reconciled,
            _ => ClearedStatus.Uncleared,
        };
    }

    // Quicken writes M/DD'YY with space padding on either side of the day and year:
    // "8/ 7'26", "4/29' 7". The apostrophe denotes the 2000s. Plain M/DD/YYYY and M/DD/YY
    // are accepted too, in case a differently-configured export ever shows up.
    internal static bool TryParseDate(string raw, out DateOnly date)
    {
        date = default;
        var cleaned = raw.Replace(" ", "").Trim();
        if (cleaned.Length == 0)
            return false;

        var apostrophe = cleaned.Contains('\'');
        var parts = cleaned.Split(['/', '\'', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var yearPart))
        {
            return false;
        }

        int year;
        if (parts[2].Length >= 4)
            year = yearPart;
        else if (apostrophe)
            year = 2000 + yearPart;
        else
            year = yearPart >= 50 ? 1900 + yearPart : 2000 + yearPart;

        if (month is < 1 or > 12 || day < 1 || year is < 1900 or > 2200)
            return false;
        if (day > DateTime.DaysInMonth(year, month))
            return false;

        date = new DateOnly(year, month, day);
        return true;
    }

    // Amounts arrive as plain decimals but tolerate thousands separators, a currency symbol,
    // and accounting-style parentheses for negatives.
    internal static bool TryParseAmount(string raw, out decimal amount)
    {
        amount = 0m;
        var s = raw.Trim();
        if (s.Length == 0)
            return false;

        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1];
        }

        s = s.Replace(",", "").Replace("$", "").Replace(" ", "").Replace("+", "");
        if (s.EndsWith('-'))
        {
            negative = true;
            s = s[..^1];
        }
        if (s.Length == 0)
            return false;

        if (!decimal.TryParse(
                s,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out amount))
        {
            return false;
        }

        if (negative)
            amount = -amount;
        return true;
    }

    // The L field is a category path that may carry a Quicken tag suffix
    // ("L[Checking]/Summer Trip") and may be a bracketed transfer reference. Without splitting
    // on the tag the importer invents junk categories.
    internal static QifCategoryRef? ParseCategoryRef(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        var isTransfer = false;

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close > 0)
            {
                isTransfer = true;
                value = value[1..close].Trim();
            }
            else
            {
                // Unterminated bracket — treat the whole thing as a transfer name.
                isTransfer = true;
                value = value[1..].Trim();
            }
        }
        else
        {
            // Everything after the first / is the tag, which the schema doesn't model.
            var slash = value.IndexOf('/');
            if (slash >= 0)
                value = value[..slash].Trim();
        }

        return value.Length == 0 ? null : new QifCategoryRef(value, isTransfer);
    }
}
