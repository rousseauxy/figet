# Contributing

Thank you for looking. FiGet is a small, opinionated server, and the fastest way to get a change in is to match how the
rest of it is written.

## Before you write code

- Read [docs/build-plan.md](docs/build-plan.md). It is the specification: what is built, in what order, and which traps
  are already known. Where the code and that document disagree, the document wins until it is changed on purpose.
- Read [CLAUDE.md](CLAUDE.md). It is the house style - layering, both database providers, protocol changes driven by
  fixtures - and it applies to people as much as to agents.
- For anything larger than a fix, open an issue first and say what you want to change. A protocol change in particular
  is a conversation: FiGet answers what real clients send, and "the specification allows it" is not by itself a reason.

## What a good change looks like

- **One change per commit**, an imperative subject line, no trailers.
- **Both database providers green.** SQLite and SQL Server both run the integration suite; a change to persistence needs
  a migration in each migration assembly.
- **A test that fails without the change.** Break your own fix on purpose, watch the test fail, then put it back. A test
  that passes either way proves nothing.
- **Comments say why, not what.** The reason a line exists, the failure that put it there, the client that needs it.
- **English everywhere**: identifiers, comments, commit messages and docs.
- **Nothing organisation-specific.** No real host names, feed names, customer data, keys or recorded traffic. Fixtures
  are synthetic or scrubbed.

## Running the tests

```
dotnet build figet.slnx -c Debug
dotnet test figet.slnx -c Debug
```

Set `FIGET_TEST_SQLSERVER` to a connection string to run the integration tests against SQL Server as well; see
[README.md](README.md). The scripts under `tests/FiGet.Compat` record real PowerShell and NuGet clients and are run by
hand on Windows.

## Reporting a problem

Open an issue with the client and its version, the request or command, what you expected and what happened. A failing
request is far more useful than a description of it: the status line, the body, and the feed's kind (curated, proxy,
asset directory).

Security problems go to [SECURITY.md](SECURITY.md) instead, not to the issue tracker.

## Licence

By contributing you agree that your contribution is licensed under the MIT licence, like the rest of the project.
