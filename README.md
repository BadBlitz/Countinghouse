<img src="src/Countinghouse.Desktop/wwwroot/logo.svg" alt="" width="72">

# Countinghouse

A self-hosted personal finance tracker — a small, focused replacement for the parts of Quicken most
people actually use: account balances, a transaction register, automatic transaction download, and
statement reconciliation.

It's a Windows desktop app — a native window, no web server and no browser. The UI is built from
Blazor components rendered in an embedded WebView2, so there is no localhost port to start and
nothing listening on your network. Your data lives in a local SQLite file and goes nowhere else.

## What it does

- **Accounts and transactions** — checking, savings, credit cards, mortgage, investment. Manual
  entry, inline editing, categories with one level of nesting.
- **QIF import** — one-time bootstrap of history from Quicken's own `File → Export → QIF` output,
  so you don't start with an empty register. Bank and credit-card accounts only.
- **Automatic sync** via [SimpleFIN Bridge](https://www.simplefin.org/), a low-cost aggregator
  aimed at hobbyist finance apps. Access URLs are encrypted at rest.
- **Reconciliation** — tick transactions against a statement, with the outstanding-check handling
  and adjusting-entry behavior you'd expect from Quicken.

A spending-by-category report is built; a net-worth-over-time report is next.

## What it deliberately doesn't do

- **No authentication, no multi-user.** It assumes one person on one machine. Don't expose it to a
  network.
- **No investment holdings.** SimpleFIN returns an account balance and a transaction list — the
  protocol has no concept of positions. Investment accounts therefore track a balance only.
- **No split transactions or auto-linked transfers** yet.

## Installing

Download `Countinghouse.Desktop-win-Setup.exe` from the latest
[release](../../releases/latest) and run it. It installs per-user with no admin prompt, adds a
desktop shortcut, and updates itself from new releases. The installer isn't code-signed, so
Windows SmartScreen will warn the first time ("More info → Run anyway").

## Building from source

Requirements:

- .NET 10 SDK (the Windows Desktop runtime comes with it)
- Windows 10 version 2004 or later. The app is WPF hosting WebView2, and the SimpleFIN credential
  is encrypted with a DPAPI-bound Data Protection key ring. The WebView2 runtime ships with
  Windows 11, so there is nothing extra to install.
- A SimpleFIN Bridge subscription (about $1.50/month) if you want automatic sync. Everything else
  works without it.

```bash
dotnet build
dotnet run --project src/Countinghouse.Desktop
```

The database and encryption keys are created under `%LOCALAPPDATA%\Countinghouse\` on first run, and
migrations are applied automatically at startup. Nothing is written inside the repo.

From there:

1. Create accounts through the UI, or drop an `accounts.json` in `%LOCALAPPDATA%\Countinghouse\` and use
   **Set Up Accounts** in the sidebar to create them in one pass — see
   [`Docs/accounts.example.json`](Docs/accounts.example.json) for the format.
2. Optionally import history from QIF exports — see
   [`Docs/Importing-From-Quicken.md`](Docs/Importing-From-Quicken.md).
3. Optionally link SimpleFIN from **SimpleFIN** in the sidebar.

## Project layout

| Project | |
|---|---|
| `Countinghouse.Domain` | Entities, EF Core context and migrations, domain services. All money math lives here. |
| `Countinghouse.SimpleFin` | SimpleFIN DTOs, HTTP client, sync service. |
| `Countinghouse.Desktop` | WPF shell hosting the Blazor components in a WebView2. |
| `Countinghouse.Tests` | xUnit, against real SQLite in-memory databases rather than mocks. |

The split is deliberate: the riskiest logic — money math and an external API contract — stays
testable without a Blazor test host.

```bash
dotnet test
```

## Contributing

Contributions are welcome. Read **[`CLAUDE.md`](CLAUDE.md)** first: it's the design document,
recording the non-obvious invariants and why they exist. Most surprising-looking code in this repo
is explained there.

A few invariants are load-bearing enough to call out here:

- A synced account's balance comes from the API and is never derived from its transactions. A
  manual account's balance is always `OpeningBalance + sum(transactions)`.
- Any code path that touches transaction rows must call `BalanceCalculator.RecalculateAsync`
  afterward, in the same `DbContext`.
- Hand-edited amounts and dates on synced rows are protected by `Transaction.IsUserModified` and
  must survive re-sync.

New behavior should come with tests. Schema changes need a real EF Core migration, never
`EnsureCreated()`.

## License

[GNU GPL v3.0](LICENSE).
