using System.Net;
using FiGet.Application.Connectors;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Domain.Feeds;

namespace FiGet.Application.Feeds;

/// <summary>An upstream as asked for. Either <see cref="Known"/> names a public gallery, or <see cref="Url"/> and <see cref="Kind"/> say where.</summary>
public sealed record UpstreamDraft
{
    public required string Name { get; init; }

    /// <summary>One of <see cref="FeedUpstream.Known"/> by name; it supplies the address and the protocol.</summary>
    public string? Known { get; init; }

    public string? Url { get; init; }

    public UpstreamKind Kind { get; init; } = UpstreamKind.V3;

    /// <summary>Package id patterns the upstream is asked for; empty for all.</summary>
    public string? Allow { get; init; }

    /// <summary>Package id patterns the upstream is never asked for.</summary>
    public string? Deny { get; init; }

    /// <summary>The environment variable holding the upstream's password; it starts with <see cref="FeedUpstream.CredentialPrefix"/>.</summary>
    public string? CredentialRef { get; init; }

    public bool Enabled { get; init; } = true;
}

public enum UpstreamChangeStatus
{
    Saved,

    /// <summary>The feed, or the upstream on it, does not exist.</summary>
    NotFound,

    /// <summary>No name, no address, or a gallery name that is not one of the known ones.</summary>
    Invalid,

    /// <summary>The credential reference does not start with the prefix or holds other characters.</summary>
    CredentialNotAllowed,

    /// <summary>Not an absolute http(s) address, or an address on a block this server never fetches from.</summary>
    UrlNotAllowed,

    /// <summary>A feed manager asked for what only an administrator may: another address, a credential, a different source.</summary>
    AdminOnly,

    /// <summary>Another upstream of the same feed has the name.</summary>
    NameTaken,
}

/// <summary>The outcome of a change to an upstream, with the upstream as it now is and the line the audit log records.</summary>
public sealed record UpstreamResult(UpstreamChangeStatus Status, FeedUpstream? Upstream = null, string? AuditDetail = null)
{
    public bool Succeeded => Status == UpstreamChangeStatus.Saved;
}

/// <summary>
/// Adding, changing, ordering and removing a feed's upstreams. These rules lived in the admin endpoints of the pages; the
/// pages and the management API now both come here. An upstream is a request the server makes from inside its network
/// with the credential it is given, so where it may point is checked here for an administrator too, and what a feed
/// manager may choose is narrower still.
/// </summary>
public sealed class UpstreamAdminService(IFeedStore feeds, UpstreamMetadataCache metadata)
{
    /// <summary>Adds an upstream last in the order. A curated feed that gains its first becomes a proxy feed.</summary>
    /// <param name="callerIsAdmin">Without it, only a known gallery without a credential.</param>
    public async Task<UpstreamResult> AddAsync(int feedKey, UpstreamDraft draft, bool callerIsAdmin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (await FindFeedAsync(feedKey, cancellationToken) is not { } feed)
        {
            return new UpstreamResult(UpstreamChangeStatus.NotFound);
        }

        if (Build(draft) is not { } upstream)
        {
            return new UpstreamResult(UpstreamChangeStatus.Invalid);
        }

        if (Refusal(upstream, before: null, callerIsAdmin) is { } refused)
        {
            return new UpstreamResult(refused);
        }

        if (!await feeds.AddUpstreamAsync(feed.Key, upstream, cancellationToken))
        {
            return new UpstreamResult(UpstreamChangeStatus.NameTaken);
        }

        // The url as well as the name: pointing a feed at a different gallery is the change worth being able to find afterwards.
        return new UpstreamResult(
            UpstreamChangeStatus.Saved,
            await FindUpstreamAsync(feed.Key, upstream.Name, cancellationToken) ?? upstream,
            $"feed={feed.Name} url={upstream.Url} kind={upstream.Kind} credentialRef={upstream.CredentialRef ?? "-"}");
    }

    /// <summary>
    /// Changes an upstream in place, keeping its place in the order. A different address, protocol or credential makes what
    /// was cached about the old source wrong, so that is forgotten and fetched again.
    /// </summary>
    public async Task<UpstreamResult> UpdateAsync(int feedKey, int upstreamKey, UpstreamDraft draft, bool callerIsAdmin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var feed = await FindFeedAsync(feedKey, cancellationToken);
        if (feed?.Upstreams.FirstOrDefault(u => u.Key == upstreamKey) is not { } before)
        {
            return new UpstreamResult(UpstreamChangeStatus.NotFound);
        }

        if (Build(draft) is not { } changed)
        {
            return new UpstreamResult(UpstreamChangeStatus.Invalid);
        }

        if (Refusal(changed, before, callerIsAdmin) is { } refused)
        {
            return new UpstreamResult(refused);
        }

        changed.Key = before.Key;
        changed.Enabled = draft.Enabled;
        switch (await feeds.UpdateUpstreamAsync(feed.Key, changed, cancellationToken))
        {
            case UpstreamChange.NameTaken:
                return new UpstreamResult(UpstreamChangeStatus.NameTaken);
            case UpstreamChange.NotFound:
                return new UpstreamResult(UpstreamChangeStatus.NotFound);
        }

        if (changed.SourceDiffersFrom(before))
        {
            metadata.ForgetUpstream(before.Key);
        }

        // Every value, and what the name was: "who pointed this feed somewhere else" is the question, and an entry under the
        // new name alone would not be found by someone looking for the old one.
        return new UpstreamResult(
            UpstreamChangeStatus.Saved,
            await FindUpstreamAsync(feed.Key, changed.Name, cancellationToken) ?? changed,
            $"feed={feed.Name} was={before.Name} url={changed.Url} kind={changed.Kind} enabled={changed.Enabled} credentialRef={changed.CredentialRef ?? "-"}");
    }

    /// <summary>
    /// Removes an upstream with the listings cached from it; packages already cached stay. The removed upstream comes back,
    /// named, because an audit entry reading "upstream 3 removed" says nothing to whoever reads it.
    /// </summary>
    public async Task<UpstreamResult> RemoveAsync(int feedKey, int upstreamKey, CancellationToken cancellationToken)
    {
        var feed = await FindFeedAsync(feedKey, cancellationToken);
        if (feed?.Upstreams.FirstOrDefault(u => u.Key == upstreamKey) is not { } removed
            || !await feeds.RemoveUpstreamAsync(feed.Key, upstreamKey, cancellationToken))
        {
            return new UpstreamResult(UpstreamChangeStatus.NotFound);
        }

        return new UpstreamResult(UpstreamChangeStatus.Saved, removed, $"feed={feed.Name} url={removed.Url}");
    }

    /// <summary>Moves an upstream one place earlier or later in the order it is asked in.</summary>
    public async Task<UpstreamResult> MoveAsync(int feedKey, int upstreamKey, bool up, CancellationToken cancellationToken)
    {
        var feed = await FindFeedAsync(feedKey, cancellationToken);
        if (feed?.Upstreams.FirstOrDefault(u => u.Key == upstreamKey) is not { } moved
            || !await feeds.MoveUpstreamAsync(feed.Key, upstreamKey, up, cancellationToken))
        {
            return new UpstreamResult(UpstreamChangeStatus.NotFound);
        }

        return new UpstreamResult(UpstreamChangeStatus.Saved, moved, $"feed={feed.Name} direction={(up ? "up" : "down")}");
    }

    /// <summary>
    /// The upstream as the draft describes it, or null without a name or an address. The patterns are kept as sent: the
    /// pattern reader splits on the separator and trims each line, so the carriage returns a textarea adds are harmless.
    /// </summary>
    private static FeedUpstream? Build(UpstreamDraft draft)
    {
        var name = draft.Name?.Trim() ?? "";
        var url = draft.Url?.Trim() ?? "";
        var kind = draft.Kind;

        // The form a feed manager gets names one of the known galleries instead of a URL and a protocol.
        if (draft.Known is { Length: > 0 } knownName)
        {
            if (FeedUpstream.Known.FirstOrDefault(k => k.Name == knownName) is not { } known)
            {
                return null;
            }

            url = known.Url;
            kind = known.Kind;
        }

        if (name.Length == 0 || url.Length == 0 || !Enum.IsDefined(kind))
        {
            return null;
        }

        return new FeedUpstream
        {
            Name = name,
            Url = url,
            Kind = kind,
            Allow = draft.Allow?.Trim() ?? "",
            Deny = draft.Deny?.Trim() ?? "",
            CredentialRef = draft.CredentialRef?.Trim() is { Length: > 0 } reference ? reference : null,
        };
    }

    /// <summary>
    /// Null when the caller may save this upstream's source. Manage on a feed covers its upstreams' names, order, patterns
    /// and switching them off, and adding one of the known public galleries. Where an upstream points and with which
    /// credential is an administrator's: the server makes that request from inside its own network, and sends the
    /// credential along.
    /// </summary>
    private static UpstreamChangeStatus? Refusal(FeedUpstream upstream, FeedUpstream? before, bool callerIsAdmin)
    {
        if (!FeedUpstream.IsAllowedCredentialRef(upstream.CredentialRef))
        {
            return UpstreamChangeStatus.CredentialNotAllowed;
        }

        // For an admin too: a gallery is an absolute http or https address, and never on the link-local block, where cloud
        // metadata services answer. A host name is not resolved here; the name an admin typed is theirs to vouch for.
        if (!Uri.TryCreate(upstream.Url, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https")
            || (address.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
                && !FetchAddresses.IsAllowed(IPAddress.Parse(address.IdnHost.Trim('[', ']')), allowPrivateNetworks: true)))
        {
            return UpstreamChangeStatus.UrlNotAllowed;
        }

        if (callerIsAdmin)
        {
            return null;
        }

        if (before is not null)
        {
            return upstream.SourceDiffersFrom(before) ? UpstreamChangeStatus.AdminOnly : null;
        }

        return upstream.CredentialRef is null && FeedUpstream.Known.Any(k => k.Url == upstream.Url && k.Kind == upstream.Kind)
            ? null
            : UpstreamChangeStatus.AdminOnly;
    }

    private async Task<Feed?> FindFeedAsync(int feedKey, CancellationToken cancellationToken) =>
        (await feeds.ListAsync(cancellationToken)).FirstOrDefault(f => f.Key == feedKey);

    private async Task<FeedUpstream?> FindUpstreamAsync(int feedKey, string name, CancellationToken cancellationToken) =>
        (await FindFeedAsync(feedKey, cancellationToken))?.Upstreams.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
}
