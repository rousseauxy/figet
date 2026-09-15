# Security

## Reporting a vulnerability

Report it privately through GitHub's **Report a vulnerability** button on the Security tab of this repository, which
opens a private advisory only the maintainers can read. Please do not open a public issue for it.

Tell us what an attacker can do, and how you got there: the request or command, the feed kind and settings involved, and
whether it needed an account, a key, or nothing at all. A proof of concept against a local instance is welcome; do not
test against anyone else's server.

FiGet is maintained by one person in their own time. Expect a first reply within a week. If a report is confirmed, the
fix, a release and the advisory go out together, and you are credited unless you ask otherwise.

## Versions

Fixes go into the latest version. There are no maintained branches for older releases.

## What counts

In scope:

- Reaching packages, files or pages without the rights for them, or from a feed a token does not cover.
- Uploading something that is served in a way that lets it run in a browser as part of the site.
- Reading a path outside an asset directory's folder, or writing one.
- Anything that lets a signed-in user act as another, or raises their level.
- The server being made to fetch from an address it should refuse.

Out of scope:

- Anything that needs the administrator's own credentials or the bootstrap token: an administrator can already change
  everything, including where upstreams point.
- A deployment's own choices - the master key left unset, anonymous read switched on, a feed's networks left open, a
  reverse proxy passing `X-Forwarded-Proto` wrongly. FiGet logs warnings for several of these;
  [running in production](docs/guide/running-in-production.md) says what to set.
- Denial of service by volume against an instance you control, and scanner output with no working request behind it.
