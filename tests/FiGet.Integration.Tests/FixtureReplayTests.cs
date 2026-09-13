using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteFixtureReplayTests(SqliteServerFixture fixture) : FixtureReplayTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerFixtureReplayTests(SqlServerServerFixture fixture) : FixtureReplayTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Replays the phase 0 recordings against FiGet and compares FiGet's answers with what the reference server
/// answered, digested by the same code that made the fixtures (<see cref="ProtocolDigest"/>).
///
/// Only scenarios whose state can be rebuilt offline are replayed: the curated-feed conversations, where the
/// packages were synthetic and are rebuilt here with the same ids, versions and tags, in the order the
/// recording scripts ran them. The proxy, paging and meta-module scenarios answered from the live PowerShell
/// Gallery and record the reference server's double-latest defect, which FiGet deliberately does not repeat;
/// those rules are pinned by the proxy tests instead. The PSResourceGet recordings against the gallery are
/// used for their filters: every one of them must parse here.
///
/// Where FiGet answers differently on purpose, the difference is listed in <see cref="Deliberate"/> with its
/// reason. Anything else that differs fails.
/// </summary>
public abstract class FixtureReplayTests
{
    private const string PowerShellModule = "FiGetRecordingTest";
    private const string NuGetExePackage = "FiGet.NuGetExe.Test";

    /// <summary>
    /// Properties the reference server adds to every v2 entry that belong to its own product - whether a package
    /// came from a connector, whether it is hosted by it - and that no NuGet client reads.
    /// </summary>
    private static readonly HashSet<string> ReferenceOnlyProperties = new(StringComparer.Ordinal)
    {
        "IsCached", "IsLocalPackage", "HasSource", "HasSymbols", "Icon",
    };

    private readonly FiGetServerFixture server;

    protected FixtureReplayTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    /// <summary>
    /// Differences FiGet makes on purpose, keyed <c>client/scenario#exchange</c>, with the reason. One exchange at a
    /// time, so an exception cannot quietly cover the rest of its scenario - and one that stops differing fails too.
    /// </summary>
    private static readonly Dictionary<string, string> Deliberate = new(StringComparer.Ordinal)
    {
        ["nugetexe-6.11.1/push-duplicate#0"] = DuplicatePush,
        ["psresourceget-1.2.0-v2/publish-psresource-duplicate#0"] = DuplicatePush,
    };

    private const string DuplicatePush =
        "the reference server silently overwrote an existing version (201); FiGet refuses with 409 unless the feed allows overwriting";

    [Fact]
    public Task PowerShellGet_2_2_5_on_a_curated_feed_gets_the_recorded_answers() =>
        ReplayAsync(
            "powershellget-2.2.5",
            [
                "register-psrepository-curated", "find-module-missing",
                "publish-module-1.0.0", "publish-module-1.1.0", "publish-module-2.0.0-beta1", "publish-module-duplicate",
                "find-module-by-name", "find-module-required-version", "find-module-all-versions", "find-module-allow-prerelease",
                "find-module-all-versions-prerelease", "find-module-wildcard", "find-module-star", "find-module-tag", "find-command",
                "save-module-latest", "save-module-required-version", "install-module-required-version", "update-module",
                "install-module-allow-prerelease", "find-package-nuget-provider-all-versions", "install-package-nuget-provider",
            ],
            version => TestPackages.Create(PowerShellModule, version, builder =>
            {
                builder.Authors.Clear();
                builder.Authors.Add("FiGet");
                builder.Description = "Synthetic module for FiGet protocol recordings";

                // The tags Publish-Module writes for this manifest: its own, then the ones it derives from it.
                foreach (var tag in new[] { "figet", "recording", "PSModule", "PSFunction_Get-FiGetRecording", "PSCommand_Get-FiGetRecording", "PSIncludes_Function" })
                {
                    builder.Tags.Add(tag);
                }
            }));

    [Fact]
    public Task NuGetExe_6_11_1_gets_the_recorded_answers() =>
        ReplayAsync(
            "nugetexe-6.11.1",
            [
                "push-1.0.0", "push-1.1.0", "push-2.0.0-beta1", "push-duplicate", "push-wrong-key",
                "list-by-name", "list-all-versions-prerelease", "search",
                "install-latest", "install-exact-version", "install-prerelease",
                "delete-1.0.0", "list-after-delete",
            ],
            version => TestPackages.Create(NuGetExePackage, version, builder =>
            {
                builder.Description = "Synthetic package for FiGet protocol recordings";
                builder.Tags.Add("figet");
                builder.Tags.Add("recording");
            }));

    [Fact]
    public Task PSResourceGet_1_2_0_publishing_to_a_feed_root_gets_the_recorded_answers() =>
        ReplayAsync(
            "psresourceget-1.2.0-v2",
            ["publish-psresource-1.0.0", "publish-psresource-1.1.0", "publish-psresource-2.0.0-beta1", "publish-psresource-duplicate"],
            version => TestPackages.Create(PowerShellModule, version));

    /// <summary>
    /// Every filter PSResourceGet sent to the PowerShell Gallery in v2 mode parses on FiGet. The answers came
    /// from the gallery, so only the parse is compared: FiGet refuses an expression it does not understand
    /// with 400, and none of these may be one.
    /// </summary>
    [Fact]
    public async Task Every_filter_PSResourceGet_sends_in_v2_mode_is_understood()
    {
        var feed = await CreateFeedAsync("replay-psrg");
        using var client = server.CreateClient();
        var failures = new List<string>();

        foreach (var (scenario, exchange) in Exchanges("psresourceget-1.2.0-v2-gallery"))
        {
            var request = exchange["request"]!;
            if ((string)request["method"]! != "GET" || !((string)request["path"]!).StartsWith("/api/v2/", StringComparison.Ordinal)
                || ((string)request["path"]!).StartsWith("/api/v2/package/", StringComparison.Ordinal))
            {
                continue;
            }

            var path = "/nuget/" + feed + (string)request["path"]!;
            using var response = await client.GetAsync(Url(path, (string?)request["query"]));
            if ((int)response.StatusCode != 200)
            {
                failures.Add($"{scenario}: {(int)response.StatusCode} for {request["path"]}?{request["query"]}: {await response.Content.ReadAsStringAsync()}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private async Task ReplayAsync(
        string clientFolder,
        IReadOnlyList<string> scenarios,
        Func<string, MemoryStream> package)
    {
        var feed = await CreateFeedAsync("replay-" + clientFolder.Split('-')[0]);
        using var client = server.CreateClient();
        var mismatches = new List<string>();
        var compared = 0;

        foreach (var scenario in scenarios)
        {
            var fixture = LoadFixture(clientFolder, scenario);
            var exchanges = fixture["exchanges"]!.AsArray();

            for (var index = 0; index < exchanges.Count; index++)
            {
                var recorded = exchanges[index]!;
                using var response = await SendAsync(client, feed, scenario, recorded["request"]!, package);
                var actual = await DigestAsync(response);
                var where = $"{scenario} #{index} {recorded["request"]!["method"]} {recorded["request"]!["path"]}";
                var differences = Compare(recorded["response"]!, actual).ToList();
                var reason = Deliberate.GetValueOrDefault($"{clientFolder}/{scenario}#{index}");
                compared++;

                if (differences.Count > 0 && reason is null)
                {
                    mismatches.AddRange(differences.Select(d => $"{where}: {d}"));
                }
                else if (differences.Count == 0 && reason is not null)
                {
                    mismatches.Add($"{where}: listed as a deliberate difference ({reason}) but now matches; remove it from the list.");
                }
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{clientFolder}: {compared} exchanges in {scenarios.Count} scenarios replayed.");
        Assert.True(compared > 0, "Nothing was replayed.");
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// Sends one recorded request. A recorded push carried a package that was never stored in the fixture, so the
    /// same id and version is built again: the version comes from the scenario name, which names it throughout.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string feed, string scenario, JsonNode request, Func<string, MemoryStream> package)
    {
        var method = new HttpMethod((string)request["method"]!);
        var path = ((string)request["path"]!).Replace("{feed}", feed, StringComparison.Ordinal);
        using var message = new HttpRequestMessage(method, Url(path, (string?)request["query"]));

        if (request["credentials"]!.AsArray().Any(c => (string?)c == "X-NuGet-ApiKey"))
        {
            message.Headers.Add("X-NuGet-ApiKey", scenario.EndsWith("wrong-key", StringComparison.Ordinal) ? "figet_wrong" : FiGetServerFixture.AdminToken);
        }

        if (request["body"] is JsonObject { } body && (string?)body["kind"] == "multipart-package")
        {
            var version = scenario switch
            {
                _ when scenario.EndsWith("-duplicate", StringComparison.Ordinal) => "1.1.0",
                _ when scenario.EndsWith("-wrong-key", StringComparison.Ordinal) => "1.0.0",
                _ => scenario[(scenario.LastIndexOf('-', scenario.IndexOf('.', StringComparison.Ordinal)) + 1)..],
            };

            var content = new MultipartFormDataContent();
            var file = new StreamContent(package(version));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(file, "package", "package.nupkg");
            message.Content = content;
        }

        return await client.SendAsync(message);
    }

    private static async Task<JsonNode> DigestAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var media = ProtocolDigest.MediaType(response.Content.Headers.ContentType?.ToString());
        var isText = media is not null && (media.Contains("xml", StringComparison.Ordinal) || media.Contains("json", StringComparison.Ordinal) || media.StartsWith("text/", StringComparison.Ordinal));
        return new JsonObject
        {
            ["status"] = (int)response.StatusCode,
            ["contentType"] = media,
            ["body"] = ProtocolDigest.Response(bytes.Length > 0, isText ? Encoding.UTF8.GetString(bytes) : null, bytes.Length, media),
        };
    }

    /// <summary>What must match: the status; for a success, the kind of answer and, per entry, what a client acts on.</summary>
    private static IEnumerable<string> Compare(JsonNode recorded, JsonNode actual)
    {
        var recordedStatus = (int)recorded["status"]!;
        var actualStatus = (int)actual["status"]!;
        if (recordedStatus != actualStatus)
        {
            yield return $"status {actualStatus}, recorded {recordedStatus}";
            yield break;
        }

        // An error body is each server's own prose, and so is the confirmation of a push or a delete: no client
        // reads either. Only the status is part of the contract there.
        if (recordedStatus >= 300 || recorded["body"]?["kind"]?.GetValue<string>() == "empty")
        {
            yield break;
        }

        var recordedBody = recorded["body"]!;
        var actualBody = actual["body"]!;
        var kind = (string?)recordedBody["kind"];
        if (kind != (string?)actualBody["kind"])
        {
            yield return $"body kind {actualBody["kind"]}, recorded {kind}";
            yield break;
        }

        if (kind == "service-document")
        {
            var expected = string.Join(",", recordedBody["collections"]!.AsArray().Select(c => (string?)c));
            var got = string.Join(",", actualBody["collections"]!.AsArray().Select(c => (string?)c));
            if (expected != got)
            {
                yield return $"collections [{got}], recorded [{expected}]";
            }

            yield break;
        }

        if (kind is not ("atom-feed" or "atom-entry"))
        {
            yield break;
        }

        if (kind == "atom-feed" && (bool?)recordedBody["nextLink"] != (bool?)actualBody["nextLink"])
        {
            yield return $"next link {actualBody["nextLink"]}, recorded {recordedBody["nextLink"]}";
        }

        var missing = (recordedBody["properties"]?.AsArray() ?? [])
            .Select(p => (string)p!)
            .Where(p => !ReferenceOnlyProperties.Contains(p))
            .Except((actualBody["properties"]?.AsArray() ?? []).Select(p => (string)p!), StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
        {
            yield return $"properties missing: {string.Join(", ", missing)}";
        }

        var recordedEntries = recordedBody["entries"]?.AsArray() ?? [];
        var actualEntries = actualBody["entries"]?.AsArray() ?? [];
        if (recordedEntries.Count != actualEntries.Count)
        {
            yield return $"entries [{Describe(actualEntries)}], recorded [{Describe(recordedEntries)}]";
            yield break;
        }

        // Matched by identity rather than position. The reference server ordered by publish time, so its silent
        // overwrite of a duplicate push moved that version to the end; FiGet orders by version. No client reads
        // the order of these entries - PowerShellGet pages until a page is empty and sorts for itself.
        var actualByKey = actualEntries.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        foreach (var recordedEntry in recordedEntries)
        {
            var key = Key(recordedEntry);
            if (!actualByKey.TryGetValue(key, out var actualEntry))
            {
                yield return $"entries [{Describe(actualEntries)}], recorded [{Describe(recordedEntries)}]";
                yield break;
            }

            foreach (var field in new[] { "normalizedVersion", "isLatestVersion", "isAbsoluteLatestVersion", "isPrerelease", "listed", "hasDependencies" })
            {
                var expected = recordedEntry![field]?.ToJsonString();
                var got = actualEntry![field]?.ToJsonString();
                if (expected != got)
                {
                    yield return $"entry {key} {field} {got ?? "absent"}, recorded {expected ?? "absent"}";
                }
            }

            var recordedSrc = ((string?)recordedEntry!["contentSrc"])?.ToLowerInvariant();
            var actualSrc = ((string?)actualEntry!["contentSrc"])?.ToLowerInvariant();
            if (recordedSrc != actualSrc)
            {
                yield return $"entry {key} content {actualSrc}, recorded {recordedSrc}";
            }
        }
    }

    private static string Key(JsonNode? entry) => $"{entry!["id"]}@{entry["version"]}";

    private static string Describe(JsonArray entries) =>
        string.Join(", ", entries.Select(e => $"{e!["id"]}@{e["version"]}"));

    private static string Url(string path, string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return path;
        }

        // Recorded decoded; re-encoded one value at a time so quotes and spaces reach the server as the client sent them.
        var pairs = query.Split('&').Select(pair =>
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            return equals < 0 ? Uri.EscapeDataString(pair) : pair[..equals] + "=" + Uri.EscapeDataString(pair[(equals + 1)..]);
        });
        return path + "?" + string.Join("&", pairs);
    }

    private async Task<string> CreateFeedAsync(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<FiGet.Application.Ports.IFeedStore>();
        Assert.True(await feeds.CreateAsync(
            new FiGet.Domain.Entities.Feed { Name = name, NameLower = name, AnonymousRead = true, CreatedUtc = DateTime.UtcNow },
            CancellationToken.None));
        return name;
    }

    private static IEnumerable<(string Scenario, JsonNode Exchange)> Exchanges(string clientFolder) =>
        Directory.GetFiles(FixturesDirectory(clientFolder), "*.json")
            .Order(StringComparer.Ordinal)
            .SelectMany(file =>
            {
                var fixture = JsonNode.Parse(File.ReadAllText(file))!;
                return fixture["exchanges"]!.AsArray().Select(e => ((string)fixture["scenario"]!, e!));
            });

    private static JsonNode LoadFixture(string clientFolder, string scenario) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(FixturesDirectory(clientFolder), scenario + ".json")))!;

    /// <summary>Walks up from the test binary to the repository, where the fixtures live.</summary>
    private static string FixturesDirectory(string clientFolder)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "figet.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not find figet.slnx above the test binary.");
        return Path.Combine(directory!.FullName, "tests", "fixtures", clientFolder);
    }
}
