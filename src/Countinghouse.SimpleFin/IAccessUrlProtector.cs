namespace Countinghouse.SimpleFin;

// Encrypts and decrypts stored Access URLs. An interface rather than a direct dependency on
// ASP.NET Core Data Protection so this class library stays free of the web stack and so the sync
// service can be tested without a key ring.
public interface IAccessUrlProtector
{
    string Protect(string accessUrl);

    // Returns false rather than throwing when the ciphertext can't be read. That case is
    // expected, not exceptional: the key ring is DPAPI-encrypted to the current Windows user, so
    // restoring the database onto a new machine or a new user account leaves every stored Access
    // URL undecryptable. It has to surface as "re-link this institution", never as a stack trace
    // or — worse — a connection that just quietly stops syncing.
    bool TryUnprotect(string encryptedAccessUrl, out string accessUrl);
}
