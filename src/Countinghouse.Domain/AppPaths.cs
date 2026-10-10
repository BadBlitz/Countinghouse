namespace Countinghouse.Domain;

// All persistent app data lives under %LOCALAPPDATA%\Countinghouse — outside the repo,
// survives rebuilds, never gets committed.
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Countinghouse");

    public static string DbPath => Path.Combine(DataDirectory, "countinghouse.db");

    // Data Protection key ring used to encrypt SimpleFIN Access URLs.
    public static string KeysDirectory => Path.Combine(DataDirectory, "keys");

    // Optional account bootstrap list for /settings/setup-accounts. It lives here rather than in
    // the repo because it names your real accounts and institutions; Docs/accounts.example.json
    // documents the shape. Absent is a normal state — the setup page then has nothing to offer and
    // accounts get created through the ordinary account form instead.
    public static string AccountPlanPath => Path.Combine(DataDirectory, "accounts.json");

    // Backups are staged here before being copied to their real destination, which doubles as a
    // local copy that survives the destination being unreachable.
    //
    // The live database deliberately stays on local disk. It runs in WAL mode, which needs shared
    // memory the OS only provides locally — SQLite's WAL does not work over a network filesystem —
    // and a sync client (OneDrive, Dropbox) uploads the .db, -wal and -shm files independently, so
    // the three can go out of sync and SQLite replays a WAL that doesn't match its database.
    // Backups are safe to put anywhere because they're written once, closed, and never reopened.
    public static string BackupStagingDirectory => Path.Combine(DataDirectory, "backups");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);

    // Data Protection won't create its own key directory, and a missing one fails at the first
    // encrypt — which is the moment a single-use setup token has just been spent.
    public static void EnsureKeysDirectory() => Directory.CreateDirectory(KeysDirectory);
}
