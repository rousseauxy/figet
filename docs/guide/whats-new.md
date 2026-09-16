# What's new

Every package feed has a **What's new** page: what it gained, and what its upstreams now offer for the packages it
holds. It answers one question — *has anything we depend on moved, and could it break something before the fleet
pulls it?* — and it can be posted to a webhook on a schedule so nobody has to remember to look.

## The three groups

| Group | What it means |
|---|---|
| **Pushed here** | A version somebody published to this feed. |
| **Cached from an upstream** | A version this feed fetched from a gallery and kept. The date is the gallery's own publish date, not the day it was fetched. |
| **Newer upstream, not fetched** | A gallery offers a version higher than anything this feed holds. **This is the early warning** — the gallery has moved ahead of what your clients install. |

Only packages this feed actually holds appear, in every group. A gallery publishing something nobody here uses is not
this feed's news; that is what separates this from reading a gallery's own list of recent packages.

A row is marked **breaking** when the major version grew — the one place a package is allowed to remove what a script
calls. It says nothing about `0.x`, where a minor version may break just as thoroughly; the version numbers are in the
row for you to read.

Two more rules worth knowing:

- **One row per package.** Among the versions a gallery published inside the window and above what this feed holds,
  only the highest is reported. A gallery that has been ahead of you for a year would otherwise be news every day.
- **Unlisted versions are left out.** A version this feed no longer offers is not news, and a cached copy the gallery
  has withdrawn is the opposite of news.

## Reading it from a script

```powershell
$report = Invoke-RestMethod "https://packages.example/api/packages/modules/changes?days=7" -Headers @{ 'X-ApiKey' = $key }
$report.changes | Where-Object breaking | Format-Table name, previousVersion, version, published
```

The route needs **Read** on the feed, like browsing it. `days` is 1 to 90 (7 by default) and anything else is clamped
rather than refused. The JSON is described in the repository's `docs/protocol-management.md`.

## Having it sent to you

An administrator sets one address under **Admin → Change reports**, and each feed can carry its own instead — that is
all "one webhook per channel" means. The address is stored encrypted and shown back as a host only, because a Teams,
Slack or Power Automate URL carries its token in its path. Set `FiGet:DataProtection:MasterKey` as well: without it the
key ring that encryption uses sits unencrypted in the database beside it.

Pick the body your receiver reads (`FiGet:Changes:Webhook:Format`):

| Format | Body | For |
|---|---|---|
| `Json` | `{ generatedUtc, target?, report: { … } }` | An automation runner, a script, a relay. |
| `Chat` | The same summary in **both** `content` and `text` | A plain Discord (`content`) or Slack (`text`) webhook, with no relay in between. The duplication is deliberate. |
| `Teams` | `type`, `attachments` with an adaptive card, and nothing else | A flow that forwards the body to Teams as a card. Nothing else may ride along, or it lands inside the card. |

A feed's **report target** (on the feed's settings page, a manager's to set) is sent as `target` in the body, for a
receiver that dispatches on it. Leave it empty and no `target` key is sent at all — the right body for a receiver with
one channel.

**Matrix** has no incoming webhook of its own: posting to a room needs a room id and an access token. Point FiGet at
the relay you already use, with `Chat` as the format, and give it the relay's bearer token through
`FiGet:Changes:Webhook:HeaderName` and `:HeaderValue`.

How often it runs is `FiGet:Jobs:ChangeReport`, a day by default. The window is "since the last report for this feed",
so a restart neither skips a day nor repeats one, and a failed post leaves that mark where it was — the next run
carries what the failed one could not. A report with nothing in it is not posted unless you ask for it: silence means
nothing moved.

## Release notes

Rows for versions this feed holds carry the release notes from the package itself. For a version only a gallery
offers, FiGet fetches the notes once when the scheduled run happens, keeps them, and shows them from then on. Two
consequences worth stating:

- Only that scheduled run fetches. Opening the page never makes this server call a gallery — otherwise anyone who may
  read a feed could aim it at one.
- **PowerShell Gallery and Chocolatey report release notes; nuget.org over v3 does not.** A v3 registration leaf
  carries none, so those rows show a dash and a link instead.

## Keeping it honest

A gallery's version list is only refreshed when somebody asks for that package — and the packages a report most wants
to talk about are the quiet ones. So a daily sweep refreshes the stored list for every id each proxy feed holds,
bounded on purpose: only ids the feed already holds, one gallery request at a time, and at most
`FiGet:Connector:SweepMaxIdsPerFeed` a run. Switch it off with `FiGet:Jobs:CatalogueSweep`.
