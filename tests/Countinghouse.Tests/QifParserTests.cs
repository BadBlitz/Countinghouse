using Countinghouse.Domain.Entities;
using Countinghouse.Domain.Qif;

namespace Countinghouse.Tests;

public class QifParserTests
{
    // ---- Field-level quirks. These are where a parser bug is most likely, and each case here
    // ---- corresponds to something actually observed in the real Quicken exports.

    [Theory]
    [InlineData("1/ 2'13", 2013, 1, 2)]     // space-padded day
    [InlineData("8/ 7'26", 2026, 8, 7)]
    [InlineData("4/29' 7", 2007, 4, 29)]    // space-padded year
    [InlineData("12/31'20", 2020, 12, 31)]
    [InlineData("3/ 1'19", 2019, 3, 1)]
    [InlineData("12/31/1998", 1998, 12, 31)] // not seen in these exports, accepted anyway
    [InlineData("3/4/99", 1999, 3, 4)]       // two-digit year without apostrophe = 1900s
    public void ParsesQuickenPaddedDates(string raw, int year, int month, int day)
    {
        Assert.True(QifParser.TryParseDate(raw, out var date));
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("13/1'20")]     // month out of range
    [InlineData("2/30'20")]     // day out of range for the month
    [InlineData("1/2")]
    public void RejectsUnparseableDates(string raw) =>
        Assert.False(QifParser.TryParseDate(raw, out _));

    [Theory]
    [InlineData("1,700.00", 1700.00)]   // thousands separator, as the real files use
    [InlineData("-31.63", -31.63)]
    [InlineData("0.37", 0.37)]
    [InlineData("$1,000.00", 1000.00)]
    [InlineData("(45.00)", -45.00)]     // accounting-style negative
    [InlineData("12.34-", -12.34)]      // trailing sign
    [InlineData("+250.00", 250.00)]
    public void ParsesAmounts(string raw, decimal expected)
    {
        Assert.True(QifParser.TryParseAmount(raw, out var amount));
        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void RejectsUnparseableAmounts(string raw) =>
        Assert.False(QifParser.TryParseAmount(raw, out _));

    [Theory]
    [InlineData("X", ClearedStatus.Reconciled)]  // the only value the real exports use
    [InlineData("R", ClearedStatus.Reconciled)]
    [InlineData("*", ClearedStatus.Cleared)]
    [InlineData("c", ClearedStatus.Cleared)]
    [InlineData("C", ClearedStatus.Cleared)]
    [InlineData("", ClearedStatus.Uncleared)]
    [InlineData(" ", ClearedStatus.Uncleared)]
    public void MapsClearedStatus(string raw, ClearedStatus expected) =>
        Assert.Equal(expected, QifParser.ParseClearedStatus(raw));

    [Fact]
    public void StripsTagSuffixFromCategoryPath()
    {
        // "LAuto/Summer Trip" — without the split the importer invents a junk category.
        var reference = QifParser.ParseCategoryRef("Auto/Summer Trip");
        Assert.Equal("Auto", reference!.Path);
        Assert.False(reference.IsTransfer);
    }

    [Fact]
    public void ReadsBracketedTransferReference()
    {
        var reference = QifParser.ParseCategoryRef("[Checking]/Summer Trip");
        Assert.Equal("Checking", reference!.Path);
        Assert.True(reference.IsTransfer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/OnlyATag")]
    public void TreatsEmptyCategoryFieldAsUncategorized(string? raw) =>
        Assert.Null(QifParser.ParseCategoryRef(raw));

    [Fact]
    public void SplitsSubcategoryPathOneLevelDeep()
    {
        Assert.Equal(("Auto", "Fuel"), new QifCategoryRef("Auto:Fuel", false).Split());
        Assert.Equal(("Auto", null), new QifCategoryRef("Auto", false).Split());
        // Deeper paths collapse into the child name — the schema supports one level.
        Assert.Equal(("Auto", "Fuel:Diesel"), new QifCategoryRef("Auto:Fuel:Diesel", false).Split());
        // A bracketed transfer name is never a path, even if it contains a colon.
        Assert.Equal(("Some: Account", null), new QifCategoryRef("Some: Account", true).Split());
    }

    // ---- Whole-file parsing against the hand-written fixtures.

    [Fact]
    public void ParsesBankFixture()
    {
        var result = QifParser.Parse(QifFixtures.Read("sample-bank.qif"));

        Assert.Empty(result.Warnings);
        Assert.Equal(["!Type:Bank"], result.AccountHeaders);
        Assert.Equal(8, result.Transactions.Count);
        Assert.Equal(1712.87m, result.Transactions.Sum(t => t.Amount));
        Assert.Equal(new DateOnly(2013, 1, 2), result.Transactions.Min(t => t.PostedDate));
        Assert.Equal(new DateOnly(2020, 12, 31), result.Transactions.Max(t => t.PostedDate));
    }

    [Fact]
    public void SkipsTagBlockWithoutMistakingTagsForCategories()
    {
        var result = QifParser.Parse(QifFixtures.Read("sample-bank.qif"));

        Assert.Equal(7, result.Categories.Count);
        Assert.DoesNotContain(result.Categories, c => c.Path == "Vacation 2019");
        Assert.DoesNotContain(result.Categories, c => c.Path == "Summer Trip");
    }

    [Fact]
    public void TakesCategoryTypeFromCatBlockFlags()
    {
        var result = QifParser.Parse(QifFixtures.Read("sample-bank.qif"));

        Assert.Equal(CategoryType.Income, result.Categories.Single(c => c.Path == "Salary").CategoryType);
        Assert.Equal(CategoryType.Expense, result.Categories.Single(c => c.Path == "Auto").CategoryType);
        // Neither E nor I set — Expense is the safer default.
        Assert.Equal(CategoryType.Expense, result.Categories.Single(c => c.Path == "Auto:Fuel").CategoryType);
    }

    [Fact]
    public void CarriesTheClearedStatusOfEachRow()
    {
        var byPayee = ParseBankByPayee();

        // The real exports mark every row CX, so this is what keeps already-reconciled
        // history out of the first reconciliation session.
        Assert.Equal(ClearedStatus.Reconciled, byPayee["Opening Balance"].ClearedStatus);
        Assert.Equal(ClearedStatus.Cleared, byPayee["Invented Contractor"].ClearedStatus);
        Assert.Equal(ClearedStatus.Uncleared, byPayee["Invented Bank"].ClearedStatus);
    }

    [Fact]
    public void ReadsTFieldAndIgnoresTheShadowUField()
    {
        var byPayee = ParseBankByPayee();
        Assert.Equal(1700.00m, byPayee["Opening Balance"].Amount);
        Assert.Equal(-42.50m, byPayee["Invented Fuel Stop"].Amount);
    }

    [Fact]
    public void ImportsRowsWithNoCategoryFieldAsUncategorized()
    {
        var byPayee = ParseBankByPayee();
        Assert.Null(byPayee["Invented Fee"].Category);
    }

    [Fact]
    public void FlattensSplitToTheParentAmountAndDominantSplitCategory()
    {
        var parent = ParseBankByPayee()["Invented Employer"];

        // Parent T is authoritative; the split amounts sum to it, so no balance is affected.
        Assert.Equal(1150.00m, parent.Amount);
        // L is the literal "--Split--" marker, so the category comes from the largest split line.
        Assert.True(parent.CategoryFromDominantSplit);
        Assert.Equal("Salary", parent.Category!.Path);
        Assert.Equal(8, parent.SplitLineCount);
    }

    [Fact]
    public void KeepsCheckNumberAndTransactionCodeFromTheNField()
    {
        var byPayee = ParseBankByPayee();
        Assert.Equal("1042", byPayee["Invented Check Deposit"].CheckNumber);
        // Quicken also puts type codes in N on bank rows; stored as-is rather than dropped.
        Assert.Equal("TXFR", byPayee["Invented Transfer Out"].CheckNumber);
    }

    [Fact]
    public void MarksBracketedReferencesAsTransfers()
    {
        var byPayee = ParseBankByPayee();
        Assert.True(byPayee["Opening Balance"].Category!.IsTransfer);
        Assert.Equal("Sample Savings", byPayee["Invented Transfer Out"].Category!.Path);
        Assert.True(byPayee["Invented Transfer Out"].Category!.IsTransfer);
        Assert.False(byPayee["Invented Fuel Stop"].Category!.IsTransfer);
    }

    [Fact]
    public void ParsesCreditCardFixture()
    {
        var result = QifParser.Parse(QifFixtures.Read("sample-ccard.qif"));

        Assert.Empty(result.Warnings);
        Assert.Equal(["!Type:CCard"], result.AccountHeaders);
        Assert.Equal(3, result.Transactions.Count);
        // Charges stay negative: QIF already uses SimpleFIN's convention, so no sign flip.
        Assert.Equal(-764.13m, result.Transactions.Sum(t => t.Amount));
    }

    [Fact]
    public void CountsInvestmentRecordsWithoutImportingThem()
    {
        var result = QifParser.Parse(QifFixtures.Read("sample-invst.qif"));

        Assert.Empty(result.Transactions);
        Assert.Equal(2, result.SkippedInvestmentRecords);
        Assert.Empty(result.AccountHeaders);
        Assert.Contains(result.Warnings, w => w.Contains("investment"));
    }

    [Fact]
    public void TrimsTrailingWhitespaceOnSectionHeaders()
    {
        // One real export writes "!Type:Bank " with a trailing space. Built inline rather than
        // in a fixture file so an editor or linter can't silently strip it.
        var text = string.Join("\r\n", ["!Type:Bank ", "D1/ 2'13", "T10.00", "PInvented", "^"]);

        var result = QifParser.Parse(text);

        Assert.Single(result.Transactions);
        Assert.Equal(["!Type:Bank"], result.AccountHeaders);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void HandlesEitherLineEnding(string newline)
    {
        var text = string.Join(newline, ["!Type:Bank", "D1/ 2'13", "T10.00", "PInvented", "^"]);
        Assert.Single(QifParser.Parse(text).Transactions);
    }

    [Fact]
    public void SkipsAndReportsRecordsMissingADateOrAmount()
    {
        var text = string.Join("\r\n", [
            "!Type:Bank",
            "T10.00", "PNo date here", "^",
            "D1/ 2'13", "PNo amount here", "^",
            "Dnonsense", "T10.00", "PBad date", "^",
            "D1/ 3'13", "T10.00", "PGood row", "^",
        ]);

        var result = QifParser.Parse(text);

        Assert.Equal("Good row", result.Transactions.Single().Payee);
        Assert.Equal(3, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Contains("Line ", w));
    }

    [Fact]
    public void IgnoresUnrecognizedSectionsAndReportsThem()
    {
        var text = string.Join("\r\n", [
            "!Type:Memorized",
            "D1/ 2'13", "T999.00", "PShould not be imported", "^",
            "!Type:Bank",
            "D1/ 3'13", "T10.00", "PShould be imported", "^",
        ]);

        var result = QifParser.Parse(text);

        Assert.Equal("Should be imported", result.Transactions.Single().Payee);
        Assert.Contains(result.Warnings, w => w.Contains("!Type:Memorized"));
    }

    [Fact]
    public void IgnoresOptionAndClearDirectivesWithoutLosingTheCurrentSection()
    {
        var text = string.Join("\r\n", [
            "!Option:AutoSwitch",
            "!Type:Bank",
            "!Clear:AutoSwitch",
            "D1/ 2'13", "T10.00", "PInvented", "^",
        ]);

        Assert.Single(QifParser.Parse(text).Transactions);
    }

    [Fact]
    public void AcceptsAFinalRecordWithNoTerminator()
    {
        var text = string.Join("\r\n", ["!Type:Bank", "D1/ 2'13", "T10.00", "PInvented"]);
        Assert.Single(QifParser.Parse(text).Transactions);
    }

    private static Dictionary<string, QifTransactionRecord> ParseBankByPayee() =>
        QifParser.Parse(QifFixtures.Read("sample-bank.qif"))
            .Transactions.ToDictionary(t => t.Payee);
}
