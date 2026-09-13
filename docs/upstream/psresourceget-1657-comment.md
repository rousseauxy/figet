# Draft: a comment for PowerShell/PSResourceGet #1657 and PR #2019

**Not posted.** Written for a person to review, adjust and post. Background and evidence are in
`docs/status.md`, "An install that fetched the wrong version".

Scope of the claims below, kept deliberately narrow: this was reproduced against a NuGet **v3** feed that
serves the gallery's own PowerShellGet versions. It was not tested against the PowerShell Gallery directly,
which PSResourceGet reaches over v2 by default; do not claim otherwise when posting.

---

Adding a reproduction with public packages, in case it helps PR #2019 along — as far as I can see the fix
currently rests on a report from a private feed.

**Environment:** PSResourceGet **1.2.0** on PowerShell 7.6.5 (Windows). Also seen on 1.1.0.1 under Windows
PowerShell 5.1, and on macOS.

**Repro**, against a NuGet v3 feed serving PowerShellGet's published versions:

```powershell
Save-PSResource -Repository <v3-feed> -Name PowerShellGet -Version 2.2.4 -Path $dir -TrustRepository
Select-String -Path "$dir/PowerShellGet/2.2.4/PowerShellGet.psd1" -Pattern ModuleVersion
```

The folder is `2.2.4`; the manifest inside says `ModuleVersion = '2.2.4.1'`.

| requested | folder created | `ModuleVersion` inside |
|---|---|---|
| 2.2.4 | 2.2.4 | **2.2.4.1** |
| 2.2.5 | 2.2.5 | **2.2.5.1** |
| 2.2.3 | 2.2.3 | 2.2.3 — control, no longer sibling |

**One thing worth stating for the fix:** `2.2.4` and `2.2.4.1` are **both listed**. So this is not about
unlisted versions, which is an easy wrong conclusion when the colliding version happens to be unlisted (as
2.2.5.1 is). It is the substring match choosing the download URL: `2.2.4` is a text prefix of `2.2.4.1`,
and with entries in descending order that is the first URL to match. The resolve step is right — the
prompt and the folder name both say 2.2.4 — and only the payload is wrong, which is visible only by
opening the installed manifest.
