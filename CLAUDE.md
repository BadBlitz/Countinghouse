# CLAUDE.md

Guidance for Claude Code (and anyone else) working in this repository.

## What this is

Countinghouse is a single-user replacement for Quicken on Windows: a WPF window hosting Blazor
components in a WebView2. There is no web server, no port and no browser. It tracks balances and
transactions, imports history from Quicken QIF exports, syncs live data through the
[SimpleFIN](https://www.simplefin.org/) aggregator, and reconciles against statements. SQLite
storage, no authentication.

Built so far: accounts and manual entry, QIF import, reconciliation (including against the synced
balance), SimpleFIN sync, balance snapshots, the net-worth and spending-by-category reports, the
register balance check, feed-health monitoring, categorization rules, backups, and the installer
with self-update.

## No personal data in the repo

The repository is public. Nothing personal goes into code, comments, tests, fixtures, docs,
commit messages or PR descriptions:

- No real balances, amounts, payees, account names or numbers, institutions, employers, or places.
  Use generic names ("Main Bank", "Card Issuer", "Brokerage", "Savings #1") and invented,
  round-ish figures.
- No readings from the live database and no incident narratives ("on this date the feed froze and
  checking was off by…"). Real data can shape a design; the write-up states the rule, not the data.
- Real exports, database copies and screenshots of real data never get committed, even
  temporarily.

### Check at every step

The rule holds at each point where text leaves the machine, not only at the PR. A leak that
reaches a pushed commit stays in the history even after the file is fixed.

`scripts/check-personal-data.sh` does the checking. It reads the real account names, institution
labels, payees and rule patterns from the live database at run time and searches for them, so
the terms themselves are never written down. Extra terms (employer, town, people) go one per
line in `private-terms.txt` in the data directory.

| When | Run |
| --- | --- |
| Before every commit, after `git add` | `scripts/check-personal-data.sh` |
| Before every push | `scripts/check-personal-data.sh develop` (the diff, the commit messages and the branch name) |
| Before opening or editing a PR | `scripts/check-personal-data.sh --file <the PR body>` |

- Exit 1 means a real account or institution name was found: fix it before going further. Exit 2
  means the check couldn't run, which is not a pass. Payee matches and dollar amounts are listed
  for a look rather than failed, because many are ordinary words and invented figures.
- **While investigating**, real names and figures will be on screen. They stay in the terminal:
  write the code, comment and test with an invented stand-in from the start, not the real value
  with a plan to swap it later.
- **Branch names, commit messages, PR titles, PR bodies and review replies** are as public as the
  code. Describe the rule that was wrong, never the account or bank that showed it.
- **Fixtures and recorded responses** are written by hand to reproduce a shape. Never paste a
  real API response, export or log excerpt and trim it down.
- **Scratch files** (database copies, dumps, saved responses, screenshots) go outside the repo,
  never in the working tree, so `git add -A` can't pick one up.
- If something does get pushed, say so at once. Fixing the file isn't enough; the history has to
  be rewritten before the branch is merged.

## Commands

```bash
dotnet build
dotnet test
dotnet test --filter FullyQualifiedName~AccountServiceTests    # one class
dotnet test --filter Name~UpdateBalance_NoChange_PostsNothing  # one test
dotnet run --project src/Countinghouse.Desktop                 # opens the app window
dotnet ef migrations add <Name> --project src/Countinghouse.Domain
```

`dotnet-ef` and `vpk` are local tools (`dotnet tool restore`). Schema changes always go through a
real EF Core migration; `EnsureCreated()` is for the tests' in-memory databases only.
`DesignTimeDbContextFactory` is what lets `dotnet ef` work without a web host to discover.

## Projects

- **`Countinghouse.Domain`**: entities, `CountinghouseContext`, migrations, and the domain services.
  All money math and business rules live here.
- **`Countinghouse.SimpleFin`**: the SimpleFIN client, sync, and the checks that depend on sync
  rules (`BalanceCheckService`, `FeedHealthService`).
- **`Countinghouse.Desktop`**: the WPF host and the Razor components. Components inject services
  directly; there is no API layer.
- **`Countinghouse.Tests`**: xUnit against real SQLite (in-memory, or on disk where a file path is
  needed), not mocks. SimpleFIN is tested with recorded JSON and a stub `HttpMessageHandler`, QIF
  with small hand-written fixtures.

### Service pattern

Services take `IDbContextFactory<CountinghouseContext>` and open a fresh context per method call.
Follow this for new services; never inject a `DbContext`. Hybrid Blazor holds one DI scope for the
life of the window, so a `Scoped` service is effectively process-lifetime; one that held a context
would leak it for the whole session.

Reads use `AsNoTracking()`. Writes re-fetch the tracked entity by id and mutate it. Never
`db.Update()` a detached instance handed back from a component.

## Runtime data

Everything lives in `%LOCALAPPDATA%\Countinghouse\` (`AppPaths`): `countinghouse.db`, the Data
Protection key ring in `keys\`, the optional `accounts.json` account plan, local backup copies in
`backups\`, and `crash.log`. Nothing is ever written inside the repo. Migrations run automatically
at startup.

- **The database runs in WAL mode.** Recent writes live in `countinghouse.db-wal`, so copying
  `countinghouse.db` while the app runs yields a valid but stale database. Close the app first or
  use `VACUUM INTO`. When restoring, delete `-wal`/`-shm` too, or a stale WAL gets replayed over
  the restored file.
- **The database must stay on local disk.** WAL needs shared memory, which a network share or a
  sync client (OneDrive, Dropbox) can't provide.
- **EF Core stores `decimal` as TEXT in SQLite.** Sums happen in memory (`ToListAsync()` then
  `Sum()`), never through SQL `SUM()`, and raw SQL comparisons need `CAST(x AS REAL)`.

## The desktop host

Several settings here look like boilerplate but break at runtime, not at build time:

- **The TFM must be `net10.0-windows10.0.19041.0`.** Without the SDK version, `BlazorWebView`
  throws `FileNotFoundException` for the WinRT projection at first paint.
- **Never set `autostart="false"` on `blazor.webview.js`** in `index.html`. The page then sits on
  its loading placeholder with no error.
- **The scoped-CSS bundle and `wwwroot` are copied to the output directory by explicit targets.**
  Miss them and the app renders unstyled. `IntermediateOutputPath` is empty in the project body,
  so the copy path is built inside the target.
- **`System.IO` and `System.Net.Http` are explicit `<Using>` items**; the Razor SDK's implicit
  usings don't include them.
- **`Program.Main` is the entry point and `App.xaml` is a `Page`**, so Velopack's install and update
  hooks run and exit before WPF starts.
- A startup failure goes to `crash.log` and a dialog, since a `WinExe` has no console. A window that
  fails to open shuts the app down; a component that throws mid-session is handled and recoverable.

## Look and feel

The design is taken from a paper account book. All of it lives in `wwwroot/app.css`, which retunes
Bootstrap's variables rather than restyling pages one by one. New pages pick it up by using plain
Bootstrap classes.

- **Red ink means a negative amount, and nothing else.** Don't colour a positive figure red because
  it's spending. Warnings use amber; errors use the red wash with a left rule.
- Page titles use Libre Caslon Text, and so does the wordmark; everything else is Public Sans with
  tabular figures, so money columns line up. Both fonts are bundled in `wwwroot/fonts` (SIL OFL)
  because the app must render offline.
- Headings, buttons and nav labels use sentence case.
- The dashboard's double rule under net worth is the one flourish. Keep other pages quiet.
- The logo is `wwwroot/logo.svg`: a house with a five-bar tally. `favicon.ico` (the exe, window
  and installer icon) carries a simplified three-bar version at 16 and 24 px, where the full
  tally blurs.

## Balances

- **Manual accounts** (`SimpleFinConnectionId` null): `CurrentBalance = OpeningBalance + sum of
  all transaction amounts`. Any code that touches `Transaction` rows or `OpeningBalance` must call
  `BalanceCalculator.RecalculateAsync(db, accountId)` afterwards, in the same `DbContext`.
- **Synced accounts**: `CurrentBalance` comes straight from SimpleFIN and is never derived from
  transactions. `RecalculateAsync` returns early for them.
- Amounts use SimpleFIN's sign convention everywhere: positive is money in.
- **Open question:** some banks publish their balance *minus scheduled outgoing payments*, so
  their feed reads low for days before a bill posts while the register is correct. Whether
  complete-history accounts should take their balance from the register rather than the feed is
  deliberately undecided. Investment accounts would keep the feed either way; they have no register.

### Balance snapshots

`BalanceSnapshotWriter` writes one `AccountBalanceSnapshot` per account per day; the last write of
the day wins, updating in place. It takes the caller's `DbContext` so a snapshot shares the unit of
work with the balance it records. SimpleFIN serves only today's balance, so a missed day is gone
for good. That's why there are three write points:

- `BalanceCalculator.RecalculateAsync`, which every manual balance change passes through.
- `SimpleFinSyncService`, on both the transaction and balance-only paths. Investment accounts have
  no other history.
- Startup, so a day with no edits and no sync is still recorded.

The row is dated from `Account.BalanceAsOf`, the bank's own timestamp, not today, so a feed that
hasn't refreshed isn't filed under the wrong day.

## QIF import

`Qif/QifParser` is pure text to records, with no I/O; `QifImportService` does preview, commit and
clear. Only `!Type:Bank` and `!Type:CCard` are imported. `!Type:Invst` is refused, because share
movements summed give uninvested cash, not value. The user guide is `Docs/Importing-From-Quicken.md`.

- **Real exports are never committed or used as fixtures.** Fixtures are hand-written and
  reproduce shapes only.
- A split row's category is the literal `--Split--`. The parent takes the category of its largest
  split line (`CategoryFromDominantSplit`), and split lines never become transactions.
- Imported rows are `Source=Manual`, `ExternalId=null` (SimpleFIN must not match them) plus an
  `ImportBatchId`, which is what `ClearImportedAsync` deletes by. Re-importing a file duplicates
  every row, because there's no stable id to dedupe on.
- Accounts with imported history open at `0.00`. Quicken exports its own opening-balance row, so a
  real opening balance would double-count. `/settings/setup-accounts` offers no balance field for
  them (`PlannedAccount.NeedsBalance`). The account plan is read from `accounts.json`
  (`AccountPlan`); a missing file means an empty plan, not an error.

## SimpleFIN sync

`SimpleFinClient` handles HTTP and token claims, `SyncCutoffCalculator` decides what not to
re-import, `SimpleFinSyncService` does the upsert, and `SimpleFinConnectionService` handles
credentials and account mapping. The UI is at `/settings/simplefin`.

- **The Access URL carries credentials inline**, and `HttpClient` ignores URL userinfo, so the Basic
  auth header is built by hand (`SimpleFinAccessUrl`). Sent as-is, the request gets a bare 403.
- **Claiming a setup token is single-use.** `ClaimAsync` stores the connection before anything
  else can fail.
- **Access URLs are encrypted with Data Protection.** The key ring is DPAPI-bound to the Windows
  user, so a database restored elsewhere can't decrypt them. `TryUnprotect` returns false rather
  than throwing, and the UI asks the user to re-link.
- **Never auto-create accounts.** An unmapped remote account is reported, not guessed at.

### Cutoffs: where data silently goes missing

One `start-date` covers every account on an Access URL, but each account's import boundary
differs. So the request is wide enough for all of them, and each account discards rows dated on
or before its own cutoff.

- The discard cutoff is `MAX(PostedDate)` over rows with an `ImportBatchId` only, and it never
  advances. A later manual row must not hide real transactions behind it.
- Coverage ("is there a hole?") is a separate question, measured over rows of any source.
- An account with no imported rows gets **no** cutoff, never a fallback to `OpeningBalanceDate`.
- **`start-date` is never omitted.** Without it the Bridge returns almost nothing, and still
  reports success.
- The lookback is 45 days (`MaxLookbackDays`; the protocol caps at 90 and warns above 45). An
  account with no rows at all gets the full 90. Anything older is reported as an **unreachable
  gap**, which only a fresh QIF export can fill. The clamp gives back a day, because the server
  measures from the current instant.
- Dates come from `transacted_at`, falling back to `posted`. For cards, `posted` is usually the
  next day, which matches neither the statement nor the QIF history.

### The upsert

- Rows with `IsUserModified` never have Amount, PostedDate or Payee overwritten.
  `TransactionService.UpdateTransactionAsync` sets the flag when a synced row's amount or date is
  edited.
- A `Reconciled` row is never walked back to `Cleared`; that would reopen a closed statement.
- Rules categorize inserted rows only; the update path never writes `CategoryId`.
- **Investment accounts sync a balance and nothing else** (`Account.SyncsTransactions`, overridable
  with `SyncTransactionsOverride`). Their feeds report share movements inconsistently (sign flips,
  missing legs). The sync deletes only `Source == SimpleFin` rows on them; QIF and manual rows stay.

## Is the feed telling the truth?

Three separate checks, each catching a failure the others can't see:

| Failure | Caught by |
| --- | --- |
| Transactions stopped, balance still updating | the register balance check |
| Everything frozen, connection still says `Success` | `BalanceNotAdvancing` |
| A quiet account's feed died unnoticed | `NoMovement` |

A connection can report success, with no errors in the API response, while the institution's
credential has failed, and the error shows only in SimpleFIN's own website. Nothing here assumes a
live balance means a live feed.

### The register balance check

`BalanceCheckService` compares `OpeningBalance + sum(posted rows)` with the balance SimpleFIN
reports, per account. It appears as the dashboard's **Register** column. It lives in the SimpleFin
project because it needs the sync's cutoff and lookback rules; duplicating those would let the
indicator drift from the truth.

- Keyed off `Transaction.Pending`, not `ClearedStatus`. The feed's balance excludes pending rows,
  and `ClearedStatus` is also the reconciliation checkbox.
- **It never writes and never auto-reconciles.** `Reconciled` is one-way, so auto-marking would
  lock the register, and a match produced by two offsetting errors would lock in bad data.
- A known unreachable gap *explains* a difference; it doesn't suppress the check. An account that
  balances despite a gap has proved nothing was lost.
- Unsynced, balance-only and never-synced accounts return `NotApplicable` with a reason.
- It sums every posted row regardless of date, while reconciliation stops at the statement end
  date, so the two can disagree about a row dated after `BalanceAsOf`, and both be right.

### Feed health

`FeedHealthService` backs the dashboard's **Feed** column and `FeedHealthBadge`, which is shared so
the views can't describe one state three ways.

- **`BalanceNotAdvancing`**: `BalanceAsOf` more than `MaxBalanceAgeDays` (3) behind `LastSyncAt`.
  A one-day lag is routine and two happens; three has only ever meant a dead feed. A weekday bank
  holiday can trip it, and that's accepted.
- **`NoMovement`**: the balance hasn't changed for longer than the account's movement expectation
  (below).
- Everything is measured to `LastSyncAt`, never to today. An account nobody synced isn't stale.
- A reported sync failure wins outright (`SyncFailing`). `Unknown` is distinct from `Ok`.
- A partial sync fails only the accounts its errors were pinned on (`Account.LastSyncError`).
  SimpleFIN's errors name an institution in free text, so the sync matches on the institution
  name; an error that matches nothing, or an account missing from the response, is blamed on
  every account rather than none.

### Movement expectations

`Account.ExpectedMovement` (`None` / `MarketDays` / `WithinDays`) plus `ExpectedMovementDays`, set at
`/settings/feed-expectations`. The expectation depends on how an account is *used*, so it can't be
derived from its type.

- `MovementExpectationSuggester` proposes a value and never applies it. The proposal is the
  longest stretch the balance went unchanged over the past year, plus a four-day margin. The
  stretch still running counts, or a newly silent account would get a window it's already
  breaking.
- Below `MinSamples` (3) movements, nothing is proposed. A dormant account can produce a confident
  but useless window, so the rationale names the gap.
- **`MarketDays` is judged on weekday observations only.** Some brokerage feeds re-serve Friday's
  balances over the weekend. Market holidays aren't modelled; `MarketDayGrace` (3 trading days)
  absorbs them.
- Investment accounts get `MarketDays` from their type. Everything defaults to `None`, so nothing
  fires until set. Setting an expectation doesn't go through `UpdateAccountAsync`, because no
  balance moves.

## Reconciliation

`ReconciliationService` + `/accounts/{id}/reconcile`.

- **The checkbox is `ClearedStatus`** (`Cleared` means ticked). `ReconciliationSessionId` is
  stamped only at finish, on rows that become `Reconciled`, so abandoning a session unwinds nothing.
- Synced rows arrive pre-ticked, so the workflow is "untick what the statement doesn't show".
  **Don't sync mid-session:** the upsert re-ticks rows that were deliberately unticked.
- Worksheet rows are `ClearedStatus != Reconciled AND PostedDate <= StatementEndDate`, with **no
  lower date bound**. An old uncleared row is an outstanding check, which is exactly what
  reconciliation exists to catch.
- The suggested starting balance is `OpeningBalance + sum(Reconciled rows)`. It stays editable, and
  `StartingBalanceDrift` shows the gap. Drift on a later statement means a reconciled row was
  edited or deleted.
- The first session must be the first statement after the QIF cutoff. Imported history is already
  `Reconciled`.
- Balance-only accounts can't reconcile. There's one open session per account, and the statement
  end date can't be in the future (the suggestion clamps to today).
- "Finish Anyway" posts a single adjustment to the "Balance Adjustment" category, dated the
  statement end, so the next statement's starting balance stays honest.

**Reconcile to the synced balance** (`ReconcileAgainstSyncedBalanceAsync`) is the same session,
not a second code path. It takes its ending balance from `CurrentBalance` and its end date from
`BalanceAsOf`, then calls the ordinary `FinishAsync` **without** an adjustment. A session that
doesn't balance stays open for the user to look at. It ticks nothing, is never triggered by a
sync, and refuses clearly when the account isn't eligible.

## Categories and rules

- One level of nesting. Names are unique per parent; `FullName` renders `Parent:Child`.
- "Balance Adjustment" (`AccountService.AdjustmentCategoryName`) is a `Transfer` singleton,
  created on first use.
- **SimpleFIN sends no categories**, so `CategoryRule` fills them in. Rules fill blanks only and
  never overwrite a category the user picked.
- Match order is derived: exact before contains, then longest pattern first. There's no priority
  column.
- `SuggestFromHistoryAsync` mines the categorized history for each payee's dominant category and
  proposes; it never applies. It only suggests payees that would fix something currently
  uncategorized, and skips payees an existing rule covers.
- Confidence is measured over the last `RecentWindowMonths` (36), falling back to all history
  below `MinRecentRows` (5). A payee's filing can drift over the years, and `DivergesFromHistory`
  flags that. All counts are shown, and small samples are highlighted.
- Payees that genuinely vary per purchase should get **no rule**. Left blank, they show up on
  `/transactions/uncategorized`. A rule would silently misfile a share of them, with nothing
  marking which.
- `TransactionService.SetCategoryAsync` skips `UpdateTransactionAsync` on purpose. A category
  can't move a balance, and it must not set `IsUserModified`.

## Reports

`ReportService` backs both report pages.

**Spending** (`/reports/spending`) groups by category over a date range.

- `Transfer` categories are excluded. Otherwise a card payment counts twice.
- Uncategorized rows get their own line rather than being dropped.
- A category counts as spending or income by the sign of its net total, so refunds don't flip it.

**Net worth** (`/reports/net-worth`) is monthly, read from balance snapshots. There is no daily
series and there won't be one: snapshots exist only on days something touched a balance, and one
reading per month is all the chart needs.

- Each account contributes its **last snapshot on or before the month's end**, even if that
  snapshot is from an earlier month (counted as "carried forward"). The closest snapshot in either
  direction is wrong: one from early next month already includes next month's activity.
- Only when an account has nothing earlier does a snapshot up to `LateSnapshotGraceDays` (5) into
  the next month stand in.
- The current month is reported as of today.
- A closed account counts through the month of its last snapshot and not after, matching the
  dashboard, which leaves closed accounts out.
- The chart starts at the first month with any snapshot. Register history isn't used to reach
  further back, because snapshot-only accounts (investments, the mortgage) would be missing from
  those months.

## Backups

`DatabaseBackupService` + `/settings/backup`. Snapshots are taken with `VACUUM INTO` into local
staging, verified (`integrity_check` plus row counts), then copied to the destination.

- Three outcomes: success, **partial** (`LocalCopyKept`: verified locally, but the destination
  was unreachable, which is a warning, not a failure), and failure.
- The "no backup in a week" reminder reads `BackupRun` rows, not the destination directory.
- **Rotation deletes only files matching `countinghouse-YYYY-MM-DD-HHMMSS.db` exactly.** The
  destination may sit beside other files. `RotateDirectory` is `internal` so this is tested
  directly.
- The destination path is stored in `AppSettings`, not a config file.

## Installer and releases

Velopack. **`main` is the production branch**: every push to it runs `.github/workflows/release.yml`,
which reads `<Version>` from `Countinghouse.Desktop.csproj`, tests, publishes self-contained
win-x64, and uploads `Countinghouse.Desktop-win-Setup.exe` plus update packages to a GitHub Release
tagged `vX.Y.Z`. To ship, bump `<Version>` on `develop` and merge into `main`. A version that's
already released is skipped with a warning, never republished.

The installed app checks for a newer release on startup (`AppUpdateService`) and downloads it in
the background. The top bar offers "Restart to update"; if that's never clicked, `VelopackApp.Run`
applies the update at next launch.

- **The pack id is `Countinghouse.Desktop`, never `Countinghouse`.** Velopack installs to
  `%LOCALAPPDATA%\<packId>` and deletes that directory on uninstall, and `%LOCALAPPDATA%\Countinghouse`
  is the data directory.
- Restart goes through `WaitExitThenApplyUpdates` + `Shutdown()`, not `ApplyUpdatesAndRestart`
  (which calls `Environment.Exit` and skips the WAL checkpoint in `OnExit`).
- Under `dotnet run` nothing is installed, and the top bar says "Dev build".
- Keep the `Velopack` package and the `vpk` tool on the same version.
- A private fork needs a read-only GitHub token in `github-token.txt` in the data directory for
  update checks. A public repo needs none.
- Releases are unsigned, so SmartScreen warns on the first `Setup.exe` run.

## Fixed identifiers

**Two Data Protection strings must never change**: `SetApplicationName("QuickenKB")` in
`App.xaml.cs` and the purpose `"QuickenKB.SimpleFin.AccessUrl.v1"` in
`DataProtectionAccessUrlProtector`. Both feed key derivation, so changing either orphans every
stored Access URL and forces every institution to be re-linked. They are identifiers, not names;
leave them alone even though they don't match the app's name.
