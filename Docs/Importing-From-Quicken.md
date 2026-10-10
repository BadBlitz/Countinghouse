# Importing from Quicken

How to bring your Quicken history into Countinghouse, so the register starts with years of
transactions instead of empty. This is a one-time job. Once it's done, SimpleFIN sync keeps things
current.

## 1. Sort your accounts into two groups

The app never stores a balance for a manual account. It works it out:

```
CurrentBalance = OpeningBalance + sum(all transactions)
```

So what you enter as the opening balance depends on whether history is coming for that account:

| Group | Accounts | Opening balance | Opening balance date |
|---|---|---|---|
| **Importing** | Checking, savings, credit cards | `0.00` | The day before the account's first Quicken transaction |
| **Snapshot** | Investment accounts, loans, a mortgage | Today's real balance (negative for money owed) | Today |

> **The trap:** typing today's real balance into an *importing* account gives you today's
> balance **plus** all of history. Nothing errors; the number is just wrong. Leaving a *snapshot*
> account at `0.00` is the opposite mistake: nothing will ever be imported to build it up.

Quicken exports each account's own "Opening Balance" row as its first transaction, which is why
starting an importing account at `0.00` lands on the right figure.

**Why investment accounts are snapshots:** their QIF history is share movements (`Buy`,
`ReinvDiv`, `XIn`), not cash flows. Summing them gives you uninvested cash, not what the account
is worth. The importer refuses `!Type:Invst` files for this reason. Their balance comes from
SimpleFIN, or from **Update Current Balance** on the account.

## 2. Create the accounts

Either one at a time through **Add Account**, or all at once:

1. Copy [`accounts.example.json`](accounts.example.json) to `%LOCALAPPDATA%\Countinghouse\accounts.json`
   and edit it to list your accounts. Set `importsHistory: true` for the importing group.
2. Open **Set Up Accounts** in the sidebar and create them in one pass.

The setup page gives importing accounts no balance field at all, so the trap above can't happen
there. Running it again is safe: accounts that already exist are left alone.

## 3. Export from Quicken

`File → Export → QIF`, **one file per account**, full date range, with **Category List**
included. The category list is the only reliable source for whether a category is income or
expense.

Keep the files outside the repo (`%LOCALAPPDATA%\Countinghouse\qif-source\` works well). They
contain your entire payee history, so never commit them or use them as test fixtures.

Quicken's export dialog can silently keep the previous account selection, which gives you two
identical files. Check before importing:

```bash
md5sum *.QIF | sort
```

## 4. Back up, then import

Take a backup first: **Backup** in the sidebar → **Back Up Now**. That's safe while the app is
running.

> If you copy the database file by hand instead, **close the app first**. The database runs in
> WAL mode, so recent writes sit in `countinghouse.db-wal`; copying just `countinghouse.db` while
> the app is open gives you a valid but out-of-date database, with no warning.

Then, for each importing account, open its register → **Import History**, upload the file, read
the preview, and commit.

- **Categories:** "Create all categories from the file's category list" (the default) brings in
  the whole list with its income/expense types. Unticked, only the categories the transactions
  use are created.
- **Splits:** Quicken writes `--Split--` as the category of a split transaction. Rather than
  create a junk category, each split takes the category of its largest line. The total is always
  Quicken's, so balances are unaffected. Split detail lines don't become transactions.
- **Everything arrives Reconciled** if Quicken marked it so (`CX`). That keeps old history out of
  your first reconciliation.

## 5. Check it against Quicken

For each imported account:

- [ ] Transaction count and date range match Quicken's register
- [ ] **Ending balance matches**, the check that matters most
- [ ] A split transaction shows the right total and no stray split rows
- [ ] Transfers to closed accounts landed as `Transfer` categories
- [ ] Restart the app and confirm the data is still there

### Starting over

Re-importing a file **duplicates every row**, because QIF rows have no stable id. To redo an
account, use **Clear imported transactions** at the bottom of its import page. It removes only
imported rows, leaving hand-entered ones and balance adjustments, and recalculates the balance.
Categories the import created stay; a re-import reuses them.

## 6. Hand over to SimpleFIN

QIF rows and SimpleFIN rows can't recognize each other, so the two must not overlap in date or
every transaction in the overlap appears twice. The sync handles this for you: for each account
it discards anything dated on or before that account's last imported transaction.

What it can't do is reach further back than the bank allows, typically 30–90 days (the app asks
for 45). So **link SimpleFIN soon after exporting**. If the export is older than that window, the
gap can only be filled by a fresh QIF export, and the sync page tells you which dates are missing.

Claim your SimpleFIN setup token early to confirm your banks are supported, and link one
institution first to prove the flow. **A setup token can be claimed only once.**
