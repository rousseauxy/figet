# Protocol fixtures

Recorded client conversations with a reference server (build plan phase 0), reduced to what a compatible
server must reproduce. One folder per client, one file per scenario.

They are produced by `tools/FiGet.Fixtures` from raw `tools/FiGet.Recorder` output. Raw recordings never enter
the repository. Fixtures carry no response bodies, no host names, no addresses and no credentials; the tool
refuses to write a fixture that looks like it does.

| Folder | Client | Reference server |
|---|---|---|
| `powershellget-2.2.5` | Windows PowerShell 5.1, PowerShellGet 2.2.5, PackageManagement 1.4.8.1 (NuGet provider 3.0.0.1), NuGet.exe 6.11.1 | Reference server (free edition) |
| `nugetexe-6.11.1` | nuget.exe 6.11.1 | Reference server (free edition) |
| `psresourceget-1.2.0-v2` | PowerShell 7.6.5, PSResourceGet 1.2.0 against a feed root without `/api/v2` | Reference server (free edition) |
| `psresourceget-1.2.0-v2-gallery` | PowerShell 7.6.5, PSResourceGet 1.2.0 in v2 mode (`/api/v2`), read-only scenarios | PowerShell Gallery |

The gallery recording rewrites the Host header to the gallery's own, so requests the client follows from absolute
URLs in responses (downloads, `next` links) went straight to the gallery and are not in that fixture set.

## Format

```json
{
  "client": "…",
  "reference": "…",
  "scenario": "find-module-by-name",
  "exchanges": [
    {
      "request": {
        "method": "GET",
        "path": "/nuget/{feed}/FindPackagesById()",
        "query": "id='FiGetRecordingTest'&$skip=0&$top=40",
        "headers": { "User-Agent": "…" },
        "credentials": [ "X-NuGet-ApiKey" ],
        "body": { "kind": "multipart-package", "length": 1234 }
      },
      "response": {
        "status": 200,
        "contentType": "application/atom+xml",
        "body": {
          "kind": "atom-feed",
          "entryCount": 3,
          "count": null,
          "nextLink": false,
          "properties": [ "Authors", "Dependencies", "…" ],
          "entries": [
            { "id": "…", "version": "1.0.0", "normalizedVersion": "1.0.0", "isLatestVersion": "false",
              "isAbsoluteLatestVersion": "false", "isPrerelease": "false", "listed": null,
              "hasDependencies": false, "contentSrc": "/nuget/{feed}/package/…/1.0.0" }
          ]
        }
      }
    }
  ]
}
```

- `path` has the feed name replaced by `{feed}`; `query` is URL-decoded.
- `credentials` lists which credential headers were sent, never their values.
- Response `body.kind` is one of `service-document` (with `collections`), `atom-feed`, `atom-entry`,
  `json-object` (top-level `properties`), `binary`, `empty`, or a media type with a length.
- Flags are kept as the strings the server sent (`"true"`, `"false"`, or `null` when absent).

## What the fixtures are for

`tests/FiGet.Integration.Tests/FixtureReplayTests.cs` replays them against FiGet, on both database providers, and
digests FiGet's answers with the same code that made the fixtures (`tests/FiGet.Testing/ProtocolDigest.cs`, also
used by `tools/FiGet.Fixtures`, so the two can never reduce an answer differently).

- **Replayed exchange by exchange**, in the order the recording scripts ran them, against a fresh feed with the
  synthetic packages rebuilt (same ids, versions and tags): the curated-feed scenarios of `powershellget-2.2.5`,
  all of `nugetexe-6.11.1` and all of `psresourceget-1.2.0-v2`. Compared: status; for a success with a body, its
  kind, service-document collections, next links, the property set (less the reference server's own product
  properties), and per entry, matched by id and version, the latest, prerelease and listed flags, whether it has
  dependencies, and its download path.
- **Differences FiGet makes on purpose** are listed in the test one exchange at a time with the reason, and an
  exception that stops being needed fails too. Today: a duplicate push, which the reference server silently
  overwrote and FiGet refuses with 409.
- **Not replayed:** the proxy, paging and meta-module scenarios answered from the live PowerShell Gallery and
  record the reference server's double-latest defect (`docs/protocol-v2.md`, "Paging and latest flags"); the
  proxy tests pin FiGet's rules there. From `psresourceget-1.2.0-v2-gallery` only the requests are used: every
  filter PSResourceGet sent must parse (200, never 400).
