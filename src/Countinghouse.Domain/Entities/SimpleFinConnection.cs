namespace Countinghouse.Domain.Entities;

// One claimed SimpleFIN Access URL; can cover multiple remote accounts under one institution login.
public class SimpleFinConnection
{
    public int Id { get; set; }
    public string InstitutionLabel { get; set; } = "";
    public string EncryptedAccessUrl { get; set; } = "";
    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncErrorMessage { get; set; }

    public List<Account> Accounts { get; set; } = [];
}
