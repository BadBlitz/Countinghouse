namespace Countinghouse.Domain.Entities;

public class Transaction
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }

    // SimpleFIN sign convention for all rows: positive = money in.
    public decimal Amount { get; set; }
    public DateOnly PostedDate { get; set; }
    public string Payee { get; set; } = "";
    public string? Memo { get; set; }
    public string? CheckNumber { get; set; }

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    public ClearedStatus ClearedStatus { get; set; }
    public bool Pending { get; set; }
    public TransactionSource Source { get; set; }

    // SimpleFIN transaction id; unique per account, the sync dedup key. Null for manual rows.
    public string? ExternalId { get; set; }

    // Protects hand-edited Amount/PostedDate from being clobbered on re-sync.
    public bool IsUserModified { get; set; }

    // Set on rows created by a QIF import run. QIF rows are otherwise indistinguishable from
    // hand-entered ones (Source=Manual, ExternalId=null), so this is what makes "clear the
    // imported history and re-import" possible without touching manual entries.
    public Guid? ImportBatchId { get; set; }

    public int? ReconciliationSessionId { get; set; }
    public ReconciliationSession? ReconciliationSession { get; set; }
}
