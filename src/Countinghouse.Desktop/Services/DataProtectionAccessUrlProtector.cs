using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Countinghouse.SimpleFin;

namespace Countinghouse.Desktop.Services;

// Encrypts SimpleFIN Access URLs with the app's Data Protection key ring (persisted under
// %LOCALAPPDATA%\Countinghouse\keys — see App.xaml.cs).
//
// On Windows that key ring is itself DPAPI-encrypted to the current Windows user account, so a
// database restored onto a new machine or a different user cannot be decrypted. That's a
// deliberate, accepted trade: an Access URL is replaceable in minutes by re-linking, unlike the
// transaction history, and storing the key ring beside the ciphertext in the same backup would
// defeat the point of encrypting it at all.
public sealed class DataProtectionAccessUrlProtector : IAccessUrlProtector
{
    // A fixed key-derivation identifier, not a display name. Changing it orphans every stored
    // Access URL, forcing a re-link of all connections.
    private const string Purpose = "QuickenKB.SimpleFin.AccessUrl.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<DataProtectionAccessUrlProtector> _logger;

    public DataProtectionAccessUrlProtector(
        IDataProtectionProvider provider, ILogger<DataProtectionAccessUrlProtector> logger)
    {
        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Protect(string accessUrl) => _protector.Protect(accessUrl);

    public bool TryUnprotect(string encryptedAccessUrl, out string accessUrl)
    {
        accessUrl = "";
        if (string.IsNullOrWhiteSpace(encryptedAccessUrl))
            return false;

        try
        {
            accessUrl = _protector.Unprotect(encryptedAccessUrl);
            return true;
        }
        catch (CryptographicException ex)
        {
            // The exception, never the value — a decrypted Access URL contains live credentials
            // and must not reach a log file.
            _logger.LogWarning(ex,
                "A stored SimpleFIN access URL could not be decrypted. The key ring is most likely " +
                "from a different Windows user or machine; the connection needs re-linking.");
            return false;
        }
    }
}
