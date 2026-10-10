using Countinghouse.Domain;
using Velopack;
using Velopack.Sources;

namespace Countinghouse.Desktop.Services;

public enum UpdateState { NotInstalled, Idle, Checking, UpToDate, Downloading, ReadyToRestart, Failed }

/// <summary>
/// Checks GitHub Releases for a newer build, downloads it in the background, and applies it on
/// restart. Registered as a singleton so the layout's banner and the startup check see one state.
/// </summary>
/// <remarks>
/// A downloaded update is applied either by <see cref="RestartToApply"/> or, if that is never
/// clicked, by <c>VelopackApp.Run</c> at the next launch — so dismissing the banner defers the
/// update rather than losing it.
/// </remarks>
public sealed class AppUpdateService
{
    public const string RepoUrl = "https://github.com/BadBlitz/Countinghouse";

    // Only needed if the repo is private: GitHub serves a private repo's releases only to an
    // authenticated caller. A fine-grained token scoped to this one repo with Contents: read-only
    // goes here. It sits beside an unencrypted database that is far more sensitive than a read-only
    // token, so it is not encrypted either. Absent is the normal case for a public repo.
    public static string TokenPath => Path.Combine(AppPaths.DataDirectory, "github-token.txt");

    private readonly UpdateManager? _manager;
    private VelopackAsset? _pending;

    public AppUpdateService()
    {
        var token = File.Exists(TokenPath) ? File.ReadAllText(TokenPath).Trim() : null;
        var manager = new UpdateManager(new GithubSource(RepoUrl, string.IsNullOrEmpty(token) ? null : token, prerelease: false));

        // Under `dotnet run` there is no installed package to update, and Velopack throws rather
        // than no-ops, so a dev build simply reports that it doesn't update itself.
        if (manager.IsInstalled)
        {
            _manager = manager;
            CurrentVersion = manager.CurrentVersion?.ToString();
            State = UpdateState.Idle;
        }
    }

    public UpdateState State { get; private set; } = UpdateState.NotInstalled;
    public string? CurrentVersion { get; }
    public string? AvailableVersion { get; private set; }
    public int DownloadPercent { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Raised off the UI thread; subscribers must marshal (InvokeAsync in a component).</summary>
    public event Action? Changed;

    public async Task CheckAsync()
    {
        if (_manager is null || State is UpdateState.Checking or UpdateState.Downloading or UpdateState.ReadyToRestart)
            return;

        try
        {
            Set(UpdateState.Checking);
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                Set(UpdateState.UpToDate);
                return;
            }

            AvailableVersion = info.TargetFullRelease.Version.ToString();
            Set(UpdateState.Downloading);
            await _manager.DownloadUpdatesAsync(info, p => { DownloadPercent = p; Changed?.Invoke(); });
            _pending = info.TargetFullRelease;
            Set(UpdateState.ReadyToRestart);
        }
        catch (Exception ex)
        {
            // A failed check is never worth interrupting anything for — an unreachable GitHub is
            // the normal case on a laptop — so it is reported, not thrown.
            Error = ex.Message;
            Set(UpdateState.Failed);
        }
    }

    /// <summary>
    /// Hands the update to Velopack's updater, which waits for this process to exit, applies it
    /// and relaunches. Exits through WPF's normal shutdown rather than
    /// <c>ApplyUpdatesAndRestart</c>, which calls <c>Environment.Exit</c> and would skip
    /// <c>App.OnExit</c> — and with it the WAL checkpoint that disposing the provider performs.
    /// </summary>
    public void RestartToApply()
    {
        if (_manager is null || _pending is null)
            return;

        _manager.WaitExitThenApplyUpdates(_pending, silent: false, restart: true);
        System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
    }

    private void Set(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed)
            Error = null;
        Changed?.Invoke();
    }
}
