using System.Text.Json;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Distribution Studio decisions: version comparison, discrepancies, platform additions, requests and sync.</summary>
public sealed class DistributionStudioTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hcg-studio-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static StoreArtifact File(string name, string platform, string arch, string format, string sha, string status = "verified") =>
        new() { Id = "art-" + name, FileName = name, Platform = platform, Format = format, Sha256 = sha, DeclaredSha256 = sha, Status = status, Architectures = [arch] };

    private static StoreRelease Rel(string id, string version, string build, string status, string channel = "stable", long published = 0, params StoreArtifact[] files) =>
        new() { Id = id, ProductId = "hcg-mail", EditionId = null, Version = version, Build = build, Status = status, Channel = channel, PublishedAt = published == 0 ? null : published, CreatedAt = published, Artifacts = files };

    private static StoreArtifactCandidate Candidate(string name, string platform, string arch, string format, string sha) =>
        new() { Path = "/x/" + name, FileName = name, Platform = platform, Format = format, Architectures = [arch], Sha256 = sha, SizeBytes = 10 };

    private static readonly string ShaA = new('a', 64), ShaB = new('b', 64), ShaC = new('c', 64);

    private static IReadOnlyList<StoreRelease> History() =>
    [
        Rel("rel-2", "1.1.0", "11", "draft", "stable", 0),
        Rel("rel-1", "1.0.0", "10", "published", "stable", 1000, File("Mail-x64.exe", "windows", "x64", "exe", ShaA), File("Mail.zip", "macos", "arm64", "zip", ShaB)),
        Rel("rel-0", "0.9.0", "9", "superseded", "stable", 500, File("Mail-old.exe", "windows", "x64", "exe", ShaC)),
    ];

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    public void Versions_compare_by_semantic_versioning(string a, string b, int expected) => Assert.Equal(expected, DistributionStudio.CompareVersions(a, b));

    [Fact]
    public void An_existing_version_and_build_is_refused_and_an_open_draft_is_continued()
    {
        var dup = DistributionStudio.Compare(null, History(), null, "stable", "1.0.0", "10", []);
        Assert.True(dup.Blocked);
        Assert.Contains(dup.Findings, f => f.Code == "duplicate");

        var cont = DistributionStudio.Compare(null, History(), null, "stable", "1.1.0", "11", []);
        Assert.False(cont.Blocked);
        Assert.Equal("rel-2", cont.ContinuesDraft?.Id);

        var revision = DistributionStudio.Compare(null, History(), null, "stable", "1.0.0", "12", []);
        Assert.Contains(revision.Findings, f => f.Code == "revision");
    }

    [Fact]
    public void A_downgrade_is_warned_and_a_newer_local_build_never_disables_the_current_release()
    {
        var c = DistributionStudio.Compare(null, History(), null, "stable", "0.9.5", "95", [], latestLocalBuild: "1.2.0");
        Assert.Equal("rel-1", c.CurrentPublished?.Id);
        Assert.Equal("newest-published", c.CurrentBasis);
        Assert.Equal("rel-2", c.LatestPrivateDraft?.Id);
        Assert.Contains(c.Findings, f => f.Code == "downgrade" && f.Level == FindingLevel.Warning);
        var newer = Assert.Single(c.Findings, f => f.Code == "newer-local");
        Assert.Contains("stays available", newer.Message);
        Assert.DoesNotContain(c.Findings, f => f.Message.Contains("disable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_current_release_named_by_HCG_Store_wins_over_the_newest_published()
    {
        var product = new StoreProduct("hcg-mail", "HCG Mail", "catalogue") { CurrentReleaseId = "rel-0" };
        var history = History().Select(r => r.Id == "rel-0" ? r with { Status = "published" } : r).ToList();
        var c = DistributionStudio.Compare(product, history, null, "stable", "1.2.0", "12", []);
        Assert.Equal("rel-0", c.CurrentPublished?.Id);
        Assert.Equal("catalogue", c.CurrentBasis);
    }

    [Fact]
    public void Artifact_conflicts_and_published_platforms_are_shown()
    {
        var files = new[] { Candidate("Mail-arm64.exe", "windows", "arm64", "exe", new string('d', 64)), Candidate("Mail-x64.exe", "windows", "x64", "exe", ShaA) };
        var c = DistributionStudio.Compare(null, History(), null, "stable", "1.2.0", "12", files);
        Assert.Contains(c.Findings, f => f.Code == "same-bytes" && f.Message.Contains("Mail-x64.exe"));
        Assert.Contains(c.Findings, f => f.Code == "supersedes-platform" && f.Message.Contains("x64") && f.Message.Contains("stays available"));
        Assert.DoesNotContain(c.Findings, f => f.Code == "supersedes-platform" && f.Message.Contains("arm64"));
        Assert.Equal(2, c.PlatformReleases.Count);
        // Another channel's releases are not compared as platforms of this channel.
        var beta = DistributionStudio.Compare(null, History(), null, "beta", "1.2.0", "12", files);
        Assert.DoesNotContain(beta.Findings, f => f.Code == "supersedes-platform");
    }

    [Fact]
    public void A_published_release_is_never_changed_a_new_revision_is_prepared_and_a_draft_takes_files()
    {
        var history = History();
        var toPublished = DistributionStudio.PlanAddition(history[1], history);
        Assert.False(toPublished.AddsToOpenDraft);
        Assert.Null(toPublished.ReleaseId);
        Assert.Equal(("1.0.0", "11"), (toPublished.Version, toPublished.Build));
        Assert.Contains("stays as it is", toPublished.Explanation);

        var toDraft = DistributionStudio.PlanAddition(history[0], history);
        Assert.True(toDraft.AddsToOpenDraft);
        Assert.Equal("rel-2", toDraft.ReleaseId);

        Assert.Equal("2026.1-r2", DistributionStudio.NextBuild("2026.1", ["2026.1"]));
        Assert.Equal("2026.1-r3", DistributionStudio.NextBuild("2026.1", ["2026.1", "2026.1-r2"]));
    }

    [Fact]
    public void Typed_metadata_that_differs_from_the_passport_is_recorded_not_hidden()
    {
        var passport = new StoreProvenance.Result("0123abcd", "local", ".NET", null, "/x/p.json", []) { Version = "1.1.9", BuildNumber = "118", Architecture = "x64", ProductName = "HCG Mail" };
        var file = Candidate("Mail.exe", "windows", "arm64", "exe", ShaA) with { Provenance = passport };
        var d = DistributionStudio.Discrepancies("1.2.0", "120", "HCG Mail", [file]);
        Assert.Contains(d, x => x.Code == "version-discrepancy" && x.Message.Contains("1.1.9") && x.Message.Contains("1.2.0"));
        Assert.Contains(d, x => x.Code == "build-discrepancy");
        Assert.Contains(d, x => x.Code == "architecture-discrepancy");
        Assert.DoesNotContain(d, x => x.Code == "name-discrepancy");
        var notes = DistributionStudio.CompatibilityNotes(["Needs Windows 11."], d);
        Assert.Equal("Needs Windows 11.", notes[0]);
        Assert.Contains(notes, n => n.StartsWith("Discrepancy: Mail.exe — Release Passport states version 1.1.9"));

        // A Debian revision or a Windows four-part version describes the same upstream version.
        var deb = Candidate("mail.deb", "ios", "arm64", "deb", ShaB) with { Package = new StorePackage("com.hcg.mail", "1.2.0-1", "iphoneos-arm64") };
        Assert.Empty(DistributionStudio.Discrepancies("1.2.0", "1", null, [deb]));
    }

    [Fact]
    public void A_publication_request_is_sent_only_to_a_Store_that_takes_it()
    {
        var on = new StoreCapabilityFlags { PublicationRequests = true, SupersedeCompatible = true };
        Assert.Equal(new StorePublicationRequest(true, "compatible"), DistributionStudio.PublicationRequest(true, true, on));
        Assert.Equal(new StorePublicationRequest(false, "compatible"), DistributionStudio.PublicationRequest(false, true, on));
        Assert.Null(DistributionStudio.PublicationRequest(false, false, on));
        Assert.Null(DistributionStudio.PublicationRequest(true, true, new StoreCapabilityFlags()));
        Assert.Null(DistributionStudio.PublicationRequest(true, true, null));
        // Without the field, older Stores (strict schemas) keep accepting the draft.
        var json = JsonSerializer.Serialize(new StoreReleaseRequest { ProductId = "p", Version = "1.0.0", Build = "1", Distribution = "free", Eligibility = "private" }, HcgStoreClientJson());
        Assert.DoesNotContain("publicationRequest", json);
    }

    private static JsonSerializerOptions HcgStoreClientJson() => new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public void Availability_actions_follow_the_release_status()
    {
        Assert.Equal(["make-current", "supersede", "disable"], DistributionStudio.AvailabilityActions("published"));
        Assert.Equal(["restore"], DistributionStudio.AvailabilityActions("withdrawn"));
        Assert.Equal(["restore"], DistributionStudio.AvailabilityActions("superseded"));
        Assert.Empty(DistributionStudio.AvailabilityActions("draft"));
    }

    [Fact]
    public void The_synchronization_center_is_built_from_the_Stores_answers()
    {
        StoreManagedRelease M(string id, string status, string? stage = null) => new() { Id = id, ProductId = "hcg-mail", ProductTitle = "HCG Mail", Version = "1.0.0", Build = id, Status = status, Stage = stage, Files = 1 };
        var groups = DistributionStudio.Sync([M("a", "draft", "uploaded"), M("b", "submitted", "awaiting-approval"), M("c", "published"), M("d", "withdrawn"), M("e", "superseded")],
            new HashSet<string> { "c" }, [("one.exe", "local"), ("two.exe", "verified"), ("three.exe", "failed")],
            [new("duplicate", FindingLevel.Blocker, "dup"), new("revision", FindingLevel.Info, "info")]).ToDictionary(g => g.Key);
        Assert.Equal(1, groups["local"].Count);
        Assert.Equal(1, groups["uploaded"].Count);
        Assert.Equal(1, groups["drafts"].Count);
        Assert.Equal(1, groups["review"].Count);
        Assert.Equal(1, groups["published"].Count);
        Assert.Equal(1, groups["current"].Count);
        Assert.Equal(2, groups["disabled"].Count);
        Assert.Equal(["three.exe (failed)"], groups["failures"].Items);
        Assert.Equal(["dup"], groups["conflicts"].Items);
    }

    [Fact]
    public void The_catalogue_filters_by_search_status_and_platform_and_uses_only_the_Stores_own_icons()
    {
        var mail = new StoreProduct("hcg-mail", "HCG Mail", "catalogue") { Summary = "Encrypted IMAP", DevelopmentStatus = "testing", Category = "communication", Platforms = [new("windows", "in-development"), new("macos", "planned")], Icon = "https://store.hakmicapitalgroup.com/media/products/hcg-mail/icon-256.webp" };
        var news = new StoreProduct("hcg-news", "HCG News", "catalogue") { DevelopmentStatus = "in-development", Platforms = [new("ios", "in-development")], Icon = "https://cdn.example.com/x.png" };
        var draft = new StoreProduct("hcg-notes", "HCG Notes", "draft");
        Assert.Equal(["hcg-mail"], StoreCatalogue.Filter([mail, news, draft], "imap", null, null).Select(p => p.Id));
        Assert.Equal(["hcg-news"], StoreCatalogue.Filter([mail, news, draft], null, "in-development", null).Select(p => p.Id));
        Assert.Equal(["hcg-notes"], StoreCatalogue.Filter([mail, news, draft], null, "draft", null).Select(p => p.Id));
        Assert.Equal(["hcg-mail"], StoreCatalogue.Filter([mail, news, draft], null, null, "macos").Select(p => p.Id));
        var origin = new Uri("https://store.hakmicapitalgroup.com/");
        Assert.NotNull(StoreCatalogue.IconUri(mail, origin));
        Assert.Null(StoreCatalogue.IconUri(news, origin));
    }

    [Fact]
    public async Task The_registration_request_is_written_locally_with_artwork_hashes_and_grants_nothing()
    {
        var icon = Path.Combine(_dir, "icon.png");
        await System.IO.File.WriteAllBytesAsync(icon, [0x89, 0x50, 0x4E, 0x47]);
        var path = await ProductRegistrationRequest.WriteAsync(_dir, new("hcg-notes", "HCG Notes", "Notes.", "productivity", ["macos"], "macos", "0.1.0", icon, null, "dev@example.test", "https://store.test/"));
        var json = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(path)).RootElement;
        Assert.Equal("hcg.product-registration-request", json.GetProperty("format").GetString());
        Assert.Equal("hcg-notes", json.GetProperty("product").GetProperty("id").GetString());
        Assert.Equal(64, json.GetProperty("artwork").GetProperty("icon").GetProperty("sha256").GetString()!.Length);
        Assert.Contains("no access is granted", json.GetProperty("note").GetString());
        Assert.Throws<ArgumentException>(() => ProductRegistrationRequest.WriteAsync(_dir, new("Bad ID", "", "", "", [], "", "x", null, null, null, null)).GetAwaiter().GetResult());
    }
}

/// <summary>The Store's answers are read as the Store sends them (found by the acceptance run against the TEST Store).</summary>
public sealed class DistributionStudioClientTests
{
    private sealed class One(int status, string body) : HttpMessageHandler
    {
        public string? Sent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Sent = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            return new((System.Net.HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task A_created_draft_product_with_platform_names_is_read()
    {
        var h = new One(201, """{"id":"hcg-notes","title":"HCG Notes","source":"draft","description":"Notes.","category":"productivity","platforms":["macos","windows"],"note":"A draft product exists only in HCG Store."}""");
        var client = new HcgStoreClient(new HttpClient(h), new Uri("https://store.test/"), _ => Task.FromResult<string?>("t"));
        var p = await client.CreateDraftProductAsync(new StoreDraftProductRequest { Id = "hcg-notes", Title = "HCG Notes", Category = "productivity", Platforms = ["macos", "windows"] });
        Assert.Equal(["macos", "windows"], p.Platforms.Select(x => x.Platform));
        Assert.Equal("draft", p.DevelopmentStatus);
        Assert.Equal("productivity", p.Category);
        Assert.DoesNotContain("\"description\"", h.Sent);   // not sent when not given (older Stores refuse unknown fields)
    }

    [Fact]
    public async Task An_availability_request_sends_the_typed_version_and_reads_the_recorded_answer()
    {
        var h = new One(202, """{"recorded":true,"note":"Recorded for the people who approve and publish in HCG Store. Nothing changed.","requests":[{"seq":7,"at":1764201600000,"by":"user-1","action":"disable","reason":"Crash on start","via":"desktop","status":"published"}]}""");
        var client = new HcgStoreClient(new HttpClient(h), new Uri("https://store.test/"), _ => Task.FromResult<string?>("t"));
        var r = await client.RequestAsync("rel-0000000000000001", "disable", "Crash on start", "1.0.0");
        Assert.True(r.Recorded);
        Assert.Equal("disable", r.Requests.Single().Action);
        Assert.Contains("\"confirm\":\"1.0.0\"", h.Sent);
    }
}

/// <summary>Version management follows the HCG Store contract it discovers: Store main's release lifecycle, the proposed
/// recorded requests, or the deployed contract (explained, never a disabled Ship to Store).</summary>
public sealed class StoreContractTests
{
    private static readonly StoreOperations MainOps = new()
    {
        Prepare = ["products.list", "releases.create"],
        Review = ["releases.approve (browser)", "releases.publish (browser)", "releases.supersede (browser)", "releases.restore (browser)", "releases.revoke (browser)"],
        Availability = ["releases.disable", "releases.enable", "releases.archive", "releases.withdraw"],
        Sync = ["manager", "changes"],
    };

    private static StoreRelease R(string status, string? availability = "available", long? published = 1) =>
        new() { Id = "rel-1", ProductId = "p", Version = "1.0.0", Build = "1", Status = status, Availability = availability, PublishedAt = published };

    [Fact]
    public void Store_main_disables_and_enables_from_Maker_and_supersedes_or_restores_in_the_browser()
    {
        var c = new StoreContract(new StoreCapabilityFlags(), MainOps);
        Assert.True(c.Lifecycle);
        Assert.True(c.PublicationChoice);
        var published = DistributionStudio.AvailabilityOptions(R("published"), c);
        Assert.Equal(AvailabilityRoute.Direct, published.Single(o => o.Action == "disable").Route);
        Assert.Equal(AvailabilityRoute.Browser, published.Single(o => o.Action == "supersede").Route);
        Assert.Equal(AvailabilityRoute.Browser, published.Single(o => o.Action == "restore").Route);
        var superseded = DistributionStudio.AvailabilityOptions(R("superseded"), c);
        Assert.Equal(AvailabilityRoute.Browser, superseded.Single(o => o.Action == "restore").Route);
        Assert.DoesNotContain(superseded, o => o.Action == "supersede");
        var disabledDraft = DistributionStudio.AvailabilityOptions(R("draft", "disabled", null), c);
        Assert.Equal(AvailabilityRoute.Direct, disabledDraft.Single(o => o.Action == "enable").Route);
        Assert.DoesNotContain(DistributionStudio.AvailabilityOptions(R("published", "revoked"), c), o => o.Action is "restore" or "enable");
        Assert.True(DistributionStudio.SupersedeOnPublish(true, false, c));
        Assert.Null(DistributionStudio.SupersedeOnPublish(false, false, c));
    }

    [Fact]
    public void The_proposed_contract_records_requests_and_the_deployed_contract_explains_without_changing_anything()
    {
        var requests = new StoreContract(new StoreCapabilityFlags { AvailabilityRequests = true, PublicationRequests = true }, null);
        Assert.True(requests.Requests);
        Assert.All(DistributionStudio.AvailabilityOptions(R("published"), requests), o => Assert.Equal(AvailabilityRoute.Request, o.Route));
        Assert.Null(DistributionStudio.SupersedeOnPublish(true, true, requests));

        var deployed = new StoreContract(new StoreCapabilityFlags { Uploads = true }, null);
        Assert.False(deployed.Lifecycle || deployed.Requests || deployed.PublicationChoice);
        var options = DistributionStudio.AvailabilityOptions(R("published"), deployed);
        Assert.Equal(["disable", "supersede", "restore"], options.Select(o => o.Action));
        Assert.All(options, o => Assert.Equal(AvailabilityRoute.Unavailable, o.Route));
        Assert.Contains("private drafts and uploads work", deployed.Explanation);
    }

    [Fact]
    public void The_current_release_is_the_one_HCG_Store_marks_current_on_a_target_and_disabled_releases_are_not_current()
    {
        StoreRelease P(string id, string v, long at, bool current, string availability = "available") => new()
        {
            Id = id, ProductId = "p", Version = v, Build = id, Status = "published", Channel = "stable", PublishedAt = at, Availability = availability,
            Targets = [new StoreReleaseTarget("windows", "x64", "stable") { Current = current }],
        };
        var (cur, basis) = DistributionStudio.CurrentPublished(null, [P("b", "2.0.0", 20, false), P("a", "1.0.0", 10, true)], null, "stable");
        Assert.Equal(("a", "current-target"), (cur?.Id, basis));
        var (none, _) = DistributionStudio.CurrentPublished(null, [P("c", "3.0.0", 30, false, "disabled")], null, "stable");
        Assert.Null(none);
    }

    [Fact]
    public async Task An_availability_change_sends_the_revision_as_If_Match()
    {
        string? ifMatch = null, path = null;
        var h = new Lambda(r => { ifMatch = r.Headers.TryGetValues("If-Match", out var v) ? v.Single() : null; path = r.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"id":"rel-1","productId":"p","version":"1.0.0","build":"1","status":"published","availability":"disabled"}""", System.Text.Encoding.UTF8, "application/json") }; });
        var client = new HcgStoreClient(new HttpClient(h), new Uri("https://store.test/"), _ => Task.FromResult<string?>("t"));
        var r = await client.ChangeAvailabilityAsync("rel-1", "disable", "Crash", "1.0.0", 7);
        Assert.Equal("\"7\"", ifMatch);
        Assert.Equal("/api/v1/publishing/releases/rel-1/disable", path);
        Assert.Equal("disabled", r.Availability);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ChangeAvailabilityAsync("rel-1", "revoke", "x", "1.0.0", 1));
    }

    private sealed class Lambda(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(f(r));
    }
}
