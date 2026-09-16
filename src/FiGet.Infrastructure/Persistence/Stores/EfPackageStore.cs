using System.Text;
using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Domain.Search;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

public sealed class EfPackageStore(FiGetDbContext db) : IPackageStore
{
    private const string LikeEscape = "\\";

    /// <summary>Values per <c>IN</c> list, kept well under SQL Server's 2100 parameters and SQLite's variable limit.</summary>
    private const int LookupBatch = 500;

    public async Task<Package?> GetPackageAsync(int feedKey, string idLower, bool includeDependencies, CancellationToken cancellationToken)
    {
        IQueryable<Package> query = db.Packages.AsNoTracking().Where(p => p.FeedKey == feedKey && p.IdLower == idLower);
        query = includeDependencies
            ? query.Include(p => p.Versions).ThenInclude(v => v.Dependencies)
            : query.Include(p => p.Versions);

        var package = await query.AsSplitQuery().FirstOrDefaultAsync(cancellationToken);
        if (package is not null)
        {
            foreach (var version in package.Versions)
            {
                version.Package = package;
                version.Dependencies.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
            }
        }

        return package;
    }

    public async Task<IReadOnlyList<Package>> ListPackagesAsync(int feedKey, CancellationToken cancellationToken)
    {
        var packages = await db.Packages
            .AsNoTracking()
            .Where(p => p.FeedKey == feedKey)
            .Include(p => p.Versions)
            .AsSplitQuery()
            .OrderBy(p => p.IdLower)
            .ToListAsync(cancellationToken);

        foreach (var package in packages)
        {
            foreach (var version in package.Versions)
            {
                version.Package = package;
            }
        }

        return packages;
    }

    public async Task<IReadOnlySet<string>> HeldIdsAsync(int feedKey, IReadOnlyCollection<string> idsLower, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idsLower);
        if (idsLower.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var held = await db.Packages.AsNoTracking()
            .Where(p => p.FeedKey == feedKey && idsLower.Contains(p.IdLower))
            .Select(p => p.IdLower)
            .ToListAsync(cancellationToken);
        return held.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ListIdsAsync(int feedKey, CancellationToken cancellationToken) =>
        await db.Packages.AsNoTracking()
            .Where(p => p.FeedKey == feedKey)
            .OrderBy(p => p.IdLower)
            .Select(p => p.IdLower)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PublishedVersion>> ListPublishedBetweenAsync(int feedKey, DateTime fromUtc, DateTime toUtc, int take, CancellationToken cancellationToken)
    {
        // The feed's packages first, then their versions by key. PackageVersions has no feed of its own, so the other
        // plan is to seek the date across every feed on the instance and throw away what belongs to the others - which
        // would make a quiet feed's page cost whatever the busiest feed is doing. Chunked like every other id lookup
        // here, to stay well under SQL Server's parameter limit.
        var packages = await db.Packages.AsNoTracking()
            .Where(p => p.FeedKey == feedKey)
            .Select(p => new { p.Key, p.Id, p.IdLower })
            .ToListAsync(cancellationToken);
        if (packages.Count == 0 || take <= 0)
        {
            return [];
        }

        var byKey = packages.ToDictionary(p => p.Key, p => p);
        var rows = new List<PublishedVersion>();
        foreach (var chunk in packages.Select(p => p.Key).Chunk(LookupBatch))
        {
            var found = await db.PackageVersions.AsNoTracking()
                .Where(v => chunk.Contains(v.PackageKey) && v.Listed && v.PublishedUtc >= fromUtc && v.PublishedUtc < toUtc)
                .OrderByDescending(v => v.PublishedUtc)
                .Take(take)
                .Select(v => new { v.PackageKey, v.NormalizedVersion, v.Origin, v.PublishedUtc, v.ReleaseNotes, v.Authors })
                .ToListAsync(cancellationToken);

            rows.AddRange(found.Select(v => new PublishedVersion(
                byKey[v.PackageKey].Id,
                byKey[v.PackageKey].IdLower,
                v.NormalizedVersion,
                v.Origin,
                v.PublishedUtc,
                v.ReleaseNotes,
                v.Authors)));
        }

        return [.. rows.OrderByDescending(r => r.PublishedUtc).ThenBy(r => r.IdLower, StringComparer.Ordinal).Take(take)];
    }

    public async Task<IReadOnlyList<HeldVersion>> ListHeldVersionsAsync(int feedKey, IReadOnlyCollection<string> idsLower, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idsLower);
        var rows = new List<HeldVersion>();
        foreach (var chunk in idsLower.Distinct(StringComparer.Ordinal).Chunk(LookupBatch))
        {
            rows.AddRange(await db.PackageVersions.AsNoTracking()
                .Where(v => v.Package!.FeedKey == feedKey && chunk.Contains(v.Package.IdLower))
                .Select(v => new HeldVersion(v.Package!.IdLower, v.NormalizedVersion, v.Listed))
                .ToListAsync(cancellationToken));
        }

        return rows;
    }

    public async Task<IReadOnlyList<Package>> ListHeldPackagesAsync(int feedKey, IReadOnlyCollection<string> idsLower, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idsLower);
        var packages = new List<Package>();
        foreach (var chunk in idsLower.Distinct(StringComparer.Ordinal).Chunk(LookupBatch))
        {
            // Projected into the entities rather than loaded as them: the caller applies rules about versions, and
            // every column it does not read is a column of description text per version of every id on the page.
            packages.AddRange(await db.Packages.AsNoTracking()
                .Where(p => p.FeedKey == feedKey && chunk.Contains(p.IdLower))
                .Select(p => new Package
                {
                    Key = p.Key,
                    FeedKey = p.FeedKey,
                    Id = p.Id,
                    IdLower = p.IdLower,
                    Versions = p.Versions
                        .Select(v => new PackageVersion
                        {
                            Key = v.Key,
                            PackageKey = v.PackageKey,
                            NormalizedVersion = v.NormalizedVersion,
                            NormalizedVersionLower = v.NormalizedVersionLower,
                            OriginalVersion = v.OriginalVersion,
                            Listed = v.Listed,
                            Origin = v.Origin,
                            PublishedUtc = v.PublishedUtc,
                        })
                        .ToList(),
                })
                .ToListAsync(cancellationToken));
        }

        return packages;
    }

    public async Task<IReadOnlyList<Package>> GetPackagesAsync(IReadOnlyCollection<long> packageKeys, CancellationToken cancellationToken)
    {
        if (packageKeys.Count == 0)
        {
            return [];
        }

        var packages = await db.Packages.AsNoTracking()
            .Where(p => packageKeys.Contains(p.Key))
            .Include(p => p.Versions)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        foreach (var package in packages)
        {
            package.Versions.ForEach(v => v.Package = package);
        }

        return packages;
    }

    public Task<PackageVersion?> GetVersionAsync(int feedKey, string idLower, string normalizedVersionLower, CancellationToken cancellationToken) =>
        db.PackageVersions.AsNoTracking()
            .Include(v => v.Package)
            .FirstOrDefaultAsync(
                v => v.Package!.FeedKey == feedKey && v.Package.IdLower == idLower && v.NormalizedVersionLower == normalizedVersionLower,
                cancellationToken);

    public async Task<SearchPage> SearchAsync(int feedKey, PackageSearchFilter filter, int skip, int take, CancellationToken cancellationToken)
    {
        var versions = db.PackageVersions.AsNoTracking().Where(v => v.Package!.FeedKey == feedKey && v.Listed);
        if (!filter.IncludePrerelease)
        {
            versions = versions.Where(v => !v.IsPrerelease);
        }

        if (!filter.IncludeSemVer2)
        {
            versions = versions.Where(v => !v.IsSemVer2);
        }

        if (!string.IsNullOrEmpty(filter.PackageTypeLower))
        {
            var needle = "|" + filter.PackageTypeLower + "|";
            versions = versions.Where(v => v.PackageTypesLower.Contains(needle));
        }

        foreach (var term in filter.Terms)
        {
            versions = ApplyTerm(versions, term);
        }

        var matchingKeys = versions.Select(v => v.PackageKey).Distinct();
        var total = await matchingKeys.CountAsync(cancellationToken);
        if (total == 0 || take <= 0)
        {
            return new SearchPage([], total);
        }

        var packages = db.Packages.AsNoTracking().Where(p => matchingKeys.Contains(p.Key));
        var keys = await Order(packages, filter)
            .Skip(skip)
            .Take(take)
            .Select(p => p.Key)
            .ToListAsync(cancellationToken);

        return new SearchPage(keys, total);
    }

    /// <summary>
    /// The order of a search. Every order ends on the id, so a page boundary between equal values - two packages with
    /// the same download count - falls in the same place on every request, and paging neither repeats nor skips one.
    /// The aggregates are over every version the package holds, the same numbers the package lists show.
    /// </summary>
    private static IOrderedQueryable<Package> Order(IQueryable<Package> packages, PackageSearchFilter filter)
    {
        var sort = filter.Sort;
        if (sort is null || sort.Field == PackageSortField.Relevance)
        {
            // An exact id match on the first free-text term ranks first, so "q=Pester" finds Pester before PesterHelper.
            var exact = filter.Terms.FirstOrDefault(t => t.Field is SearchField.Any or SearchField.Id or SearchField.PackageId && !t.Value.Contains('*', StringComparison.Ordinal))?.Value ?? "";
            return packages.OrderBy(p => p.IdLower == exact ? 0 : 1).ThenBy(p => p.IdLower);
        }

        var ordered = (sort.Field, sort.Descending) switch
        {
            (PackageSortField.Id, false) => packages.OrderBy(p => p.IdLower),
            (PackageSortField.Id, true) => packages.OrderByDescending(p => p.IdLower),
            (PackageSortField.Versions, false) => packages.OrderBy(p => p.Versions.Count),
            (PackageSortField.Versions, true) => packages.OrderByDescending(p => p.Versions.Count),
            (PackageSortField.Downloads, false) => packages.OrderBy(p => p.Versions.Sum(v => v.Downloads)),
            (PackageSortField.Downloads, true) => packages.OrderByDescending(p => p.Versions.Sum(v => v.Downloads)),
            (PackageSortField.LastPublished, false) => packages.OrderBy(p => p.Versions.Max(v => v.PublishedUtc)),
            _ => packages.OrderByDescending(p => p.Versions.Max(v => v.PublishedUtc)),
        };

        return sort.Field == PackageSortField.Id ? ordered : ordered.ThenBy(p => p.IdLower);
    }

    public async Task<IReadOnlyList<string>> AutocompleteIdsAsync(int feedKey, string query, bool includePrerelease, bool includeSemVer2, int skip, int take, CancellationToken cancellationToken)
    {
        var lower = query.Trim().ToLowerInvariant();
        var versions = db.PackageVersions.AsNoTracking().Where(v => v.Package!.FeedKey == feedKey && v.Listed);
        if (!includePrerelease)
        {
            versions = versions.Where(v => !v.IsPrerelease);
        }

        if (!includeSemVer2)
        {
            versions = versions.Where(v => !v.IsSemVer2);
        }

        if (lower.Length > 0)
        {
            versions = versions.Where(v => v.Package!.IdLower.Contains(lower));
        }

        var matchingKeys = versions.Select(v => v.PackageKey).Distinct();
        return await db.Packages.AsNoTracking()
            .Where(p => matchingKeys.Contains(p.Key))
            .OrderBy(p => p.IdLower.StartsWith(lower) ? 0 : 1)
            .ThenBy(p => p.IdLower)
            .Skip(skip)
            .Take(take)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AddVersionAsync(int feedKey, string id, PackageVersion version, bool replaceExisting, CancellationToken cancellationToken)
    {
        var idLower = id.ToLowerInvariant();

        // Two attempts: a concurrent first push of the same id can win the unique index on the package row.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                var package = await db.Packages.FirstOrDefaultAsync(p => p.FeedKey == feedKey && p.IdLower == idLower, cancellationToken);
                if (package is null)
                {
                    package = new Package { FeedKey = feedKey, Id = id, IdLower = idLower };
                    db.Packages.Add(package);
                    await db.SaveChangesAsync(cancellationToken);
                }

                var existing = await db.PackageVersions
                    .Where(v => v.PackageKey == package.Key && v.NormalizedVersionLower == version.NormalizedVersionLower)
                    .Select(v => new { v.Key, v.Downloads })
                    .FirstOrDefaultAsync(cancellationToken);
                if (existing is not null)
                {
                    if (!replaceExisting)
                    {
                        return false;
                    }

                    version.Downloads = existing.Downloads;
                    await db.PackageVersions.Where(v => v.Key == existing.Key).ExecuteDeleteAsync(cancellationToken);
                }

                version.PackageKey = package.Key;
                db.PackageVersions.Add(version);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
                version.Key = 0;
                version.Dependencies.ForEach(d => d.Key = 0);
            }
            catch (DbUpdateException)
            {
                // "Already exists" only when the version is there now - a concurrent push of the same version won the unique
                // index. Anything else (a value the column refuses, a broken constraint) is a failure to store, and
                // reporting it as a duplicate told a client its new version existed when nothing had been written.
                db.ChangeTracker.Clear();
                if (await db.PackageVersions.AnyAsync(
                        v => v.NormalizedVersionLower == version.NormalizedVersionLower && v.Package!.FeedKey == feedKey && v.Package.IdLower == idLower,
                        cancellationToken))
                {
                    return false;
                }

                throw;
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<PackageVersion>> ListUnlistedAsync(int feedKey, int skip, int take, CancellationToken cancellationToken) =>
        await db.PackageVersions
            .AsNoTracking()
            .Include(v => v.Package)
            .Where(v => v.Package!.FeedKey == feedKey && !v.Listed)
            .OrderByDescending(v => v.LastUpdatedUtc)
            .ThenBy(v => v.Key)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<bool> SetListedAsync(int feedKey, string idLower, string normalizedVersionLower, bool listed, CancellationToken cancellationToken) =>
        await db.PackageVersions
            .Where(v => v.Package!.FeedKey == feedKey && v.Package.IdLower == idLower && v.NormalizedVersionLower == normalizedVersionLower)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Listed, listed), cancellationToken) > 0;

    public async Task<bool> SetPublishedAsync(int feedKey, string idLower, string normalizedVersionLower, DateTime publishedUtc, CancellationToken cancellationToken) =>
        await db.PackageVersions
            .Where(v => v.Package!.FeedKey == feedKey && v.Package.IdLower == idLower && v.NormalizedVersionLower == normalizedVersionLower)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.PublishedUtc, publishedUtc).SetProperty(v => v.LastUpdatedUtc, publishedUtc), cancellationToken) > 0;

    public async Task<bool> DeleteVersionAsync(int feedKey, string idLower, string normalizedVersionLower, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.PackageVersions
            .Where(v => v.Package!.FeedKey == feedKey && v.Package.IdLower == idLower && v.NormalizedVersionLower == normalizedVersionLower)
            .Select(v => new { v.Key, v.PackageKey })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return false;
        }

        await db.SymbolFiles.Where(s => s.PackageVersionKey == row.Key).ExecuteDeleteAsync(cancellationToken);
        await db.PackageDependencies.Where(d => d.PackageVersionKey == row.Key).ExecuteDeleteAsync(cancellationToken);
        await db.PackageVersions.Where(v => v.Key == row.Key).ExecuteDeleteAsync(cancellationToken);
        if (!await db.PackageVersions.AnyAsync(v => v.PackageKey == row.PackageKey, cancellationToken))
        {
            await db.Packages.Where(p => p.Key == row.PackageKey).ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task IncrementDownloadsAsync(long packageVersionKey, DateTime utcNow, CancellationToken cancellationToken) =>
        db.PackageVersions
            .Where(v => v.Key == packageVersionKey)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Downloads, v => v.Downloads + 1).SetProperty(v => v.LastUsedUtc, utcNow), cancellationToken);

    public async Task<IReadOnlyList<RetentionCandidate>> ListRetentionCandidatesAsync(int feedKey, CancellationToken cancellationToken) =>
        await db.PackageVersions
            .AsNoTracking()
            .Where(v => v.Package!.FeedKey == feedKey)
            .Select(v => new RetentionCandidate(v.Package!.Id, v.Package.IdLower, v.NormalizedVersion, v.IsPrerelease, v.Listed, v.Origin, v.LastUsedUtc, v.Size))
            .ToListAsync(cancellationToken);

    public async Task ReplaceSymbolFilesAsync(long packageVersionKey, IReadOnlyList<SymbolFile> files, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SymbolFiles.Where(s => s.PackageVersionKey == packageVersionKey).ExecuteDeleteAsync(cancellationToken);
        db.SymbolFiles.AddRange(files);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<SymbolFile>> GetSymbolFilesAsync(long packageVersionKey, CancellationToken cancellationToken) =>
        await db.SymbolFiles.AsNoTracking().Where(s => s.PackageVersionKey == packageVersionKey).ToListAsync(cancellationToken);

    public Task<SymbolFile?> FindSymbolFileAsync(int feedKey, string fileNameLower, string symbolKeyLower, CancellationToken cancellationToken) =>
        db.SymbolFiles.AsNoTracking().FirstOrDefaultAsync(
            s => s.FeedKey == feedKey && s.FileNameLower == fileNameLower && s.SymbolKeyLower == symbolKeyLower,
            cancellationToken);

    private static IQueryable<PackageVersion> ApplyTerm(IQueryable<PackageVersion> versions, SearchTerm term)
    {
        var value = term.Value;
        var wildcard = value.Contains('*', StringComparison.Ordinal);
        var pattern = wildcard ? ToLikePattern(value) : null;

        return term.Field switch
        {
            SearchField.PackageId => wildcard
                ? versions.Where(v => EF.Functions.Like(v.Package!.IdLower, pattern!, LikeEscape))
                : versions.Where(v => v.Package!.IdLower == value),
            SearchField.Id => wildcard
                ? versions.Where(v => EF.Functions.Like(v.Package!.IdLower, pattern!, LikeEscape))
                : versions.Where(v => v.Package!.IdLower.Contains(value)),
            SearchField.Tag => wildcard
                ? versions.Where(v => EF.Functions.Like(v.TagsLower, "% " + pattern + " %", LikeEscape))
                : versions.Where(v => v.TagsLower.Contains(" " + value + " ")),
            _ => wildcard
                ? versions.Where(v => EF.Functions.Like(v.SearchTextLower, "%" + pattern + "%", LikeEscape))
                : versions.Where(v => v.SearchTextLower.Contains(value)),
        };
    }

    /// <summary>Escapes LIKE metacharacters and turns <c>*</c> into <c>%</c>.</summary>
    private static string ToLikePattern(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            switch (c)
            {
                case '*':
                    builder.Append('%');
                    break;
                case '%' or '_' or '[' or '\\':
                    builder.Append('\\').Append(c);
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
