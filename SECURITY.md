# Security policy

## Reporting a vulnerability

Please report security problems privately, not in a public issue. On this repository's
**Security** tab, choose **Report a vulnerability**. That opens a private advisory only the
maintainer can see.

Useful things to include: the version (shown in the app's top bar), what you did, what happened,
and what you expected instead.

**Don't send real financial data.** No database files, SimpleFIN setup tokens or Access URLs, QIF
exports, or screenshots showing real accounts. Describe the shape of the data, or reproduce the
problem with invented figures.

This is a one-person project maintained in spare time. Reports are read and taken seriously, but
there is no guaranteed response time and no bounty.

## Supported versions

Only the latest release is supported. The installed app updates itself, so a fix ships as a new
release, never as a patch to an old one.

## What is by design

Countinghouse is a single-user desktop app. It runs no server and listens on no port, and it
trusts the Windows account it runs under. These are deliberate, so they aren't vulnerabilities
on their own:

- **No sign-in.** Anyone who can use your Windows account can open the app.
- **The database is not encrypted.** It is an ordinary SQLite file in
  `%LOCALAPPDATA%\Countinghouse`, and so are its backups, wherever you point them. Protect them
  the way you protect the rest of your profile: disk encryption and a locked account.
- **SimpleFIN Access URLs are encrypted for the current Windows user.** Another user, or a copy
  of the database on another machine, can't read them. Software already running as you can.
- **Releases are not code-signed**, so Windows SmartScreen warns on the first run of the
  installer. Download it only from this repository's Releases page.

## What is worth reporting

- An Access URL, setup token or account data ending up somewhere it shouldn't: a log, the crash
  file, an error message, a request to anywhere other than SimpleFIN or GitHub.
- A crafted QIF file or SimpleFIN response that does more than fail to import, such as running
  code, writing outside the data directory, or corrupting existing data.
- A way for the self-update to install something other than a release from this repository.
- A weakness in how the Access URL is protected at rest.
