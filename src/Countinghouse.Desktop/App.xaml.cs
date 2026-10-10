using System.Windows;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Countinghouse.Desktop.Services;
using Countinghouse.Domain;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Services;
using Countinghouse.SimpleFin;

namespace Countinghouse.Desktop;

/// <summary>
/// Builds the dependency-injection container, opens the database, and shows the main window.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unhandled component exception would otherwise take the process down with an empty
        // window and no explanation. Hybrid marshals component work onto the WPF dispatcher, so
        // this one handler catches both UI-thread and component exceptions.
        DispatcherUnhandledException += (_, args) =>
        {
            ReportFatal("Unhandled exception", args.Exception);
            args.Handled = true;
        };

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif

        AppPaths.EnsureDataDirectory();
        AppPaths.EnsureKeysDirectory();
        services.AddDbContextFactory<CountinghouseContext>(options =>
            options.UseSqlite($"Data Source={AppPaths.DbPath}"));

        // Without this the key ring lands in ASP.NET's default per-user location and rotates out
        // from under the stored SimpleFIN Access URLs. Pinning it beside the database keeps the
        // whole app's state in one directory. SetApplicationName matters too: the default is
        // derived from the content root path, so changing either the app name or this path would
        // make every previously-encrypted ciphertext unreadable. The name is a fixed key-derivation
        // identifier, not a display name: it must never change, whatever the app is called.
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(AppPaths.KeysDirectory))
            .SetApplicationName("QuickenKB");

        // Scoped is kept rather than rewritten to Singleton. Hybrid creates one scope per
        // BlazorWebView and holds it for the life of the window, so these are effectively
        // process-lifetime — which is safe only because every one of them takes IDbContextFactory
        // and opens a fresh DbContext per call rather than holding one. A service that captured a
        // DbContext would now leak it for the whole session.
        services.AddScoped<AccountService>();
        services.AddScoped<TransactionService>();
        services.AddScoped<CategoryService>();
        services.AddScoped<CategoryRuleService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<QifImportService>();
        services.AddScoped<ReportService>();
        services.AddScoped<MovementExpectationSuggester>();
        // Read once at startup; the file is optional and names real accounts, so it lives outside the repo.
        services.AddSingleton(AccountPlan.LoadDefault());
        services.AddScoped<AccountSetupService>();
        services.AddSingleton(new BackupPaths(AppPaths.DbPath, AppPaths.BackupStagingDirectory));
        services.AddScoped<DatabaseBackupService>();

        services.AddSingleton<IAccessUrlProtector, DataProtectionAccessUrlProtector>();
        // A bank that stops responding mid-request shouldn't leave the page spinning indefinitely.
        services.AddHttpClient<SimpleFinClient>(http => http.Timeout = TimeSpan.FromSeconds(60));
        services.AddScoped<SimpleFinConnectionService>();
        services.AddScoped<SimpleFinSyncService>();
        services.AddScoped<BalanceCheckService>();
        services.AddScoped<FeedHealthService>();
        services.AddSingleton<AppUpdateService>();

        _services = services.BuildServiceProvider();

        // A desktop app that fails before its first window is just a process that never appeared,
        // so the one thing that must not happen is failing silently.
        try
        {
            InitializeDatabase();
        }
        catch (Exception ex)
        {
            ReportFatal($"Could not open the database at {AppPaths.DbPath}", ex);
            Shutdown(1);
            return;
        }

        // Separate from the handler above, which keeps the app alive: a component that throws
        // mid-session is recoverable by navigating away, but a window that never opens leaves a
        // process running with nothing on screen and no way to reach it except Task Manager.
        try
        {
            new MainWindow(_services).Show();
        }
        catch (Exception ex)
        {
            ReportFatal("The main window could not be opened", ex);
            Shutdown(1);
            return;
        }

        // After the window, never before it: a slow or unreachable GitHub must not delay first
        // paint, and a failed check is shown in the layout rather than raised.
        _ = _services.GetRequiredService<AppUpdateService>().CheckAsync();
    }

    /// <summary>
    /// Migrates to the current schema and records today's balance snapshots, before the window is
    /// shown. Blocking is deliberate: both steps must finish before a component can read the
    /// database, and neither is slow against a local SQLite file.
    /// </summary>
    private void InitializeDatabase()
    {
        using var scope = _services!.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CountinghouseContext>>();
        using var db = dbFactory.CreateDbContext();
        db.Database.Migrate();

        // A balance nobody looked at today is still a fact about today, and snapshots can't be
        // backfilled later — the aggregator only serves the current figure. Recording at startup
        // means the net-worth series has a point for every day the app was opened, not only days
        // with a sync.
        BalanceSnapshotWriter.RecordAllAsync(db).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Shows a failure and writes it to <c>crash.log</c> beside the database.
    /// </summary>
    /// <remarks>
    /// A WinExe has no console and nothing is captured anywhere by default, so without this the
    /// only evidence of a failure is a window that never appeared.
    /// </remarks>
    private static void ReportFatal(string what, Exception ex)
    {
        var logPath = Path.Combine(AppPaths.DataDirectory, "crash.log");

        try
        {
            AppPaths.EnsureDataDirectory();
            var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}";
            File.AppendAllText(logPath, entry + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // Losing the log is not worth replacing the real error with a logging error.
        }

        MessageBox.Show(
            $"{what}{Environment.NewLine}{Environment.NewLine}{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{Environment.NewLine}Details written to:{Environment.NewLine}{logPath}",
            "Countinghouse — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // SQLite is in WAL mode, so disposing the provider checkpoints and removes the -wal/-shm
        // sidecars. Leaving them behind is what makes a file-copy backup silently stale.
        _services?.Dispose();
        base.OnExit(e);
    }
}
