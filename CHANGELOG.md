# Changelog

What each release delivered. The GitHub release for a tag is made from its section here by CI, so a tag is pushed only
once its section is written. Releases up to 1.7.1 have their notes on the GitHub releases page only.

Images: `ghcr.io/rousseauxy/figet:<version>`, `linux/amd64` and `linux/arm64`.

## 1.8.3 - unreleased

No migration, no new setting.

- **Gallery publish dates are right on a server outside UTC.** A v2 gallery writes its publish dates as UTC without a
  time zone; they were read as the server's local time, so on a server in Brussels every cached version was dated one
  hour early in winter and two in summer. Dates already cached are corrected the next time the id is listed.
- **A gallery file that fails its own hash says so.** The download is still refused with 502, but the message now says
  the upstream's file does not match the hash it publishes, and there is no `Retry-After`: asking again gets the same
  file. It used to say "try again later". Seen for some 2017 versions on the PowerShell Gallery.

## 1.8.2 - 2026-11-26

On SQL Server, upgrading changes the collation of two columns on `PackageVersions`; the migration runs on start-up as
usual. Nothing changes on SQLite.

- **Search is several times faster on SQL Server.** `v3/query` took 1.5 to 3 seconds on an idle instance holding a
  gallery-sized feed. The search text is now compared byte for byte (it is stored lower-cased already, so results are
  the same), and a search reads the full text of only the version each result shows instead of every version's. On a
  test feed of 7,300 versions, searches went from 1.3-4.0 s to 0.1-0.7 s. The v2 `Search()` gains from the first part.

## 1.8.1 - 2026-11-26

From a load test on the work QA instance. No migration, no new required setting.

- **Requests that went wrong are logged even with the request log off.** An answer of 500 or more, a client that left
  before or during the answer, and a request slower than `FiGet:Logging:SlowRequestSeconds` (default 5) to send its
  first byte are each one warning line. These were invisible: clients saw timeouts, cut-off answers and 404s, and the
  server's log said nothing. With `FiGet:Logging:Requests` on, every line also says how the request ended and its time
  to the first byte.
- **An upstream that fails is no longer "not found".** A download of a version a proxy feed does not hold, which its
  upstream failed to serve, answers 502 (504 when it did not answer in time) with `Retry-After: 60`, so Install-Module
  reports a gallery problem rather than a missing package. A version the upstream answers it does not have is still 404.

## 1.8.0 - 2026-11-25

Upgrading adds two columns (on `PackageVersions` and `JobLeases`); the migration runs on start-up as usual.

- **Who published a version.** A push now records the token that made it - its name, or `user/key` for a personal
  key, as the audit log names it - or the signed-in account, and `/api/packages/{feed}/versions` returns it as
  `publishedBy`. Versions pushed before this release have none; cached copies stay `SYSTEM`.
- **A bell for tokens that end soon.** Signed in, the top bar shows a bell with a count when one of your personal keys
  ends within 14 days, and for an administrator also when a service token does. It leads to the page where the key
  can be replaced, and the profile page now marks such a key as the tokens page already did.
- **The jobs table says what each job did.** Under "Last run" on Admin → System: what the last run removed, sent or
  warned about, or why it failed, with how long it ran on hover. The token expiry warning is listed with the other jobs;
  it was missing. A run that throws is now recorded and logged and the job carries on at its next round.
- **HEAD on package downloads.** `HEAD` on the v3 flat container `.nupkg` and the v2 `/package/{id}/{version}` answered
  405; it now answers like `GET`, without a body and without counting a download.
- **A sign-in provider that cannot be reached** sends the person back to the sign-in page (or the profile, when
  connecting a provider) with a message, instead of an HTTP 500. The failure is in the audit log as
  `signin.external.unreachable`.
- **Notices** no longer have extra space above their first line.
- **CI makes the GitHub release** for each `v*` tag from this file, after the images are published.

## 1.7.7 - 2026-11-19

- Every JSON log line carries a UTC `Timestamp`, so a saved log can be put in order against client tests.

## 1.7.6 - 2026-11-19

- The Appearance page no longer fails (HTTP 500) before its stored theme is read, which showed on SQL Server.
- The System page and the firewall guide list the extra hosts Entra ID and Google sign-in need.
- A prerelease is marked as such on the package and version pages.
- The daily image check uses Trivy instead of osv-scanner. .NET SDK 10.0.404.

## 1.7.5 - 2026-11-13

- The release image builds on the Dockerfile's own runtime pin, so it carries ASP.NET Core 10.0.15.
- NuGet client packages 8.0.0, OpenTelemetry 1.21, and the other package and action updates of the week.

## 1.7.4 - 2026-11-11

- .NET 10.0.15 security release: runtime, SDK and Microsoft packages (CVE-2026-114146, CVE-2026-114147,
  CVE-2026-114149). OpenTelemetry 1.20.1.

## 1.7.3 - 2026-10-27

- Rebuilt on the patched base image (ca-certificates, USN-9001-1). Same code as 1.7.2.

## 1.7.2 - 2026-10-27

- .NET 10.0.14 security release: runtime, SDK and Microsoft packages.
