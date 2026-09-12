# Architecture decision records

Short records of choices that cost real money to rediscover.

This directory is deliberately small. It is not a log of every decision — most of those are explained in
a comment next to the code they affect, which is where they belong. What lands here is the narrower set
that meets **all three** of these:

1. **The reason is not visible from the code.** Someone reading the line sees *what*, not *why*.
2. **The obvious-looking change is wrong.** A newcomer tidying up would break something, and the
   breakage would not be obvious for a while.
3. **Rediscovering it costs a day or more.** Usually because the failure is intermittent, remote, or
   looks like a different problem entirely.

Anything that fails one of those tests is a code comment, not an ADR.

## Format

`NNNN-short-title.md`, with **Context** (what was happening), **Decision** (what we did) and
**Consequences** (what this costs, and what would have to change to revisit it). Records are
append-only: when a decision is reversed, add a new record and mark the old one superseded rather than
editing it. The record of a decision that turned out badly is more useful than no record.

## Index

| | | |
|---|---|---|
| [0001](0001-domain-may-reference-nuget-versioning.md) | The domain may reference NuGet.Versioning, and nothing else | Version comparison is the domain, not a detail |
