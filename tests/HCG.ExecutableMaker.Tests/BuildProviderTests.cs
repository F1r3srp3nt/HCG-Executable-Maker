using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.BuildEngine.BuildHost;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Tests;

/// <summary>The New Build wizard's Build Provider: discovery, project matching, authorization, request, host key, credentials.</summary>
public class BuildProviderTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();
    private const string Commit = "e4b2cacaa56b40fba341f05cbb1d2f6874b67f88";

    [Fact]
    public void The_selector_lists_local_build_host_and_github_actions_which_is_not_implemented()
    {
        Assert.Equal([BuildProviderKind.LocalMachine, BuildProviderKind.HcgServerBuildHost, BuildProviderKind.GitHubActions], BuildProviderCatalog.All.Select(o => o.Kind));
        Assert.True(BuildProviderCatalog.LocalMachine.Implemented && BuildProviderCatalog.HcgServer.Implemented);
        Assert.False(BuildProviderCatalog.GitHubActions.Implemented);
        Assert.Contains("sends no source files", BuildProviderCatalog.HcgServer.Description, StringComparison.Ordinal);
        Assert.Equal(BuildProviderIds.HcgServerBuildHost, BuildProviderCatalog.HcgServer.Id);
    }

    [Theory]
    [InlineData("https://github.com/F1r3srp3nt/HCG-LifeOS.git", "F1r3srp3nt/HCG-LifeOS")]
    [InlineData("https://user:secret@github.com/F1r3srp3nt/HCG-LifeOS", "F1r3srp3nt/HCG-LifeOS")]
    [InlineData("git@github.com:F1r3srp3nt/HCG-Executable-Maker.git", "F1r3srp3nt/HCG-Executable-Maker")]
    [InlineData("ssh://git@github.com/F1r3srp3nt/hcg-server", "F1r3srp3nt/hcg-server")]
    [InlineData("https://gitlab.com/a/b.git", null)]
    [InlineData("http://127.0.0.1:1234/git/F1r3srp3nt/x", null)]
    [InlineData(null, null)]
    public void GitHub_repository_from_an_origin_url_never_keeps_credentials(string? url, string? expected) =>
        Assert.Equal(expected, GitRevision.GitHubRepository(url));

    [Fact]
    public void Origin_url_is_read_from_git_config()
    {
        string[] config = ["[core]", "\tbare = false", "[remote \"upstream\"]", "\turl = https://github.com/other/repo", "[remote \"origin\"]", "\turl = https://github.com/F1r3srp3nt/HCG-LifeOS.git", "\tfetch = +refs/heads/*:refs/remotes/origin/*"];
        Assert.Equal("https://github.com/F1r3srp3nt/HCG-LifeOS.git", GitRevision.OriginUrl(config));
        Assert.Null(GitRevision.OriginUrl(["[remote \"upstream\"]", "url = x"]));
    }

    private ProjectRecord Imported(string sub, string? origin = "https://github.com/F1r3srp3nt/HCG-LifeOS.git", string commit = Commit)
    {
        var repo = Path.Combine(_t.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(repo, ".git", "refs", "heads", "main"), commit + "\n");
        File.WriteAllText(Path.Combine(repo, ".git", "config"), origin is null ? "[core]\n" : $"[remote \"origin\"]\n\turl = {origin}\n");
        var src = Path.Combine(repo, sub.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(src);
        var ws = Path.Combine(_t.Path, "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        var p = new ProjectRecord { Id = Guid.NewGuid(), Name = "LifeOS", SourceKind = ProjectSourceKind.Directory, SourceLocation = src, WorkspacePath = ws, ImportedAt = DateTimeOffset.UtcNow };
        ImportRevisions.WriteAsync(p, ImportRevisions.Capture(p, TimeProvider.System), CancellationToken.None).GetAwaiter().GetResult();
        return p;
    }

    private static BuildProject HostProject(string id, string repo, string remote, string? path, string provider = "go") =>
        new() { Id = id, Name = id, Source = new BuildSource { Repository = repo, Remote = remote }, Path = path, Provider = provider, Operations = ["resolve", "build", "test", "package"] };

    [Fact]
    public void The_source_is_the_working_copy_folder_github_repository_and_import_commit()
    {
        var project = Imported("apps/helper");
        var s = BuildHostSource.For(project);
        Assert.Equal(Path.Combine(_t.Path, "repo"), s.RepositoryRoot);
        Assert.Equal("apps/helper", s.RelativePath);
        Assert.Equal("F1r3srp3nt/HCG-LifeOS", s.Repository);
        Assert.Equal(Commit, s.Commit);
        Assert.True(s.HasPinnedCommit);
        Assert.Equal(RevisionState.Current, s.Revision.State);
    }

    [Fact]
    public void Registered_projects_are_matched_by_working_copy_then_repository_and_folder()
    {
        var s = BuildHostSource.For(Imported("apps/helper"));
        var repo = Path.Combine(_t.Path, "repo");
        var matches = BuildProviderPlanning.Match(s, [
            HostProject("unrelated", "Someone/Else", "/src/else", null),
            HostProject("other-folder", "F1r3srp3nt/HCG-LifeOS", "/src/lifeos", "apps/other"),
            HostProject("same-repo", "F1r3srp3nt/HCG-LifeOS", "https://github.com/F1r3srp3nt/HCG-LifeOS", "apps/helper"),
            HostProject("same-copy", "F1r3srp3nt/HCG-LifeOS", repo, "apps/helper/"),
        ]);
        Assert.Equal(["same-copy", "same-repo", "other-folder", "unrelated"], matches.Select(m => m.Project.Id));
        Assert.Equal([true, true, false, false], matches.Select(m => m.IsMatch));
        Assert.Contains("approved this working copy", matches[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_not_from_a_git_working_copy_has_no_pinned_commit_and_no_request()
    {
        var ws = Path.Combine(_t.Path, "plain-ws");
        var src = Path.Combine(_t.Path, "plain");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(src);
        var p = new ProjectRecord { Id = Guid.NewGuid(), Name = "Plain", SourceKind = ProjectSourceKind.Directory, SourceLocation = src, WorkspacePath = ws, ImportedAt = DateTimeOffset.UtcNow };
        ImportRevisions.WriteAsync(p, ImportRevisions.Capture(p, TimeProvider.System), CancellationToken.None).GetAwaiter().GetResult();
        var s = BuildHostSource.For(p);
        Assert.False(s.HasPinnedCommit);
        Assert.Null(s.RepositoryRoot);
        var ex = Assert.Throws<ArgumentException>(() => BuildProviderPlanning.Request(p, HostProject("x", "a/b", "/r", null), "", ["build"], new(), _t.Path, "1.0.0"));
        Assert.Contains("40-character commit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_request_uses_the_registered_project_and_only_operations_the_owner_allowed()
    {
        var p = Imported("apps/helper");
        var host = HostProject("helper", "F1r3srp3nt/HCG-LifeOS", "/src", "apps/helper") with { Operations = ["build", "test"] };
        var r = BuildProviderPlanning.Request(p, host, Commit.ToUpperInvariant(), ["build", "test"], new BuildHostOptions { Configuration = "release" }, _t.Path, "1.2.3");
        Assert.Equal("helper", r.BuildHostProjectId);
        Assert.Equal(Commit, r.Commit);
        Assert.Equal(p.Id, r.ProjectId);
        Assert.Equal(["build", "test"], r.Operations);
        Assert.Equal("release", r.Options.Configuration);
        Assert.Equal("1.2.3", r.Settings.Identity.Version);
        Assert.Contains(Commit[..12], r.Purpose, StringComparison.Ordinal);
        Assert.Equal(["build", "test"], BuildProviderPlanning.AllowedOperations(host));
        Assert.Throws<ArgumentException>(() => BuildProviderPlanning.Request(p, host, Commit, ["package"], new(), _t.Path, "1.0.0"));
        Assert.Throws<ArgumentException>(() => BuildProviderPlanning.Request(p, host, Commit, [], new(), _t.Path, "1.0.0"));
    }

    [Fact]
    public void The_authorization_names_host_commit_source_operations_and_that_no_source_is_sent()
    {
        var host = HostProject("helper", "F1r3srp3nt/HCG-LifeOS", "/Users/owner/Developer/lifeos", "apps/helper");
        var text = BuildProviderPlanning.AuthorizationStatement(host, Commit, ["build", "package"], new Uri("http://127.0.0.1:7600/buildhost/v1/"), "e8ff884ad1ea88c9e3f269550c772ef8c8fba363990a84c909a3262a846b15f0");
        foreach (var part in new[] { "http://127.0.0.1:7600", Commit[..12], "F1r3srp3nt/HCG-LifeOS", "apps/helper", "/Users/owner/Developer/lifeos", "build, package", "go provider", "sends no source files", "owner must still approve", "unsigned", "e8ff884ad1ea88c9" })
            Assert.Contains(part, text, StringComparison.Ordinal);
        Assert.NotEqual(text, BuildProviderPlanning.AuthorizationStatement(host, Commit, ["build"], new Uri("http://127.0.0.1:7600/buildhost/v1/"), null));
    }

    [Fact]
    public void The_host_key_is_checked_against_the_pin()
    {
        const string fp = "e8ff884ad1ea88c9e3f269550c772ef8c8fba363990a84c909a3262a846b15f0";
        Assert.Equal(SigningCheckOutcome.Warning, BuildProviderPlanning.KeyCheck(fp, null).Outcome);
        Assert.Equal(SigningCheckOutcome.Passed, BuildProviderPlanning.KeyCheck(fp, "E8FF 884A D1EA 88C9 E3F2 6955 0C77 2EF8 C8FB A363 990A 84C9 09A3 262A 846B 15F0").Outcome);
        Assert.Equal(SigningCheckOutcome.Failed, BuildProviderPlanning.KeyCheck(fp, new string('a', 64)).Outcome);
        Assert.Equal("E8FF 884A D1EA 88C9 E3F2 6955 0C77 2EF8 C8FB A363 990A 84C9 09A3 262A 846B 15F0", BuildProviderPlanning.Group(fp));
    }

    [Fact]
    public void Targets_follow_the_provider_and_the_host_from_verified_provenance()
    {
        var known = new KnownBuildHost { BaseUrl = "http://127.0.0.1:7600/buildhost/v1/", KeyFingerprint = "aa", OperatingSystem = "darwin", Architecture = "arm64" };
        Assert.Contains("darwin/arm64", BuildProviderPlanning.Targets("go", known), StringComparison.Ordinal);
        Assert.Contains("first verified build", BuildProviderPlanning.Targets("dotnet", null), StringComparison.Ordinal);
        Assert.Contains("iOS", BuildProviderPlanning.Targets("xcode", known), StringComparison.Ordinal);
        Assert.Contains("unsigned", BuildProviderPlanning.Targets("xcode", null), StringComparison.Ordinal);
        Assert.Equal("go version go1.25.4 linux/amd64", BuildProviderPlanning.ToolVersion(new BuildToolchain { Tool = "go", Version = "go version go1.25.4 linux/amd64" }));
        Assert.Equal("dotnet 10.0.114", BuildProviderPlanning.ToolVersion(new BuildToolchain { Tool = "dotnet", Version = "10.0.114" }));
    }

    private static HcgBuildHostClient Client(HttpMessageHandler host, BuildHostCredentials credentials) =>
        new(new HttpClient(host), credentials, new HcgBuildHostSettings { PollInterval = TimeSpan.FromMilliseconds(1) });

    [Fact]
    public async Task Connecting_needs_a_token_and_reports_status_and_registered_projects()
    {
        using var host = new FakeBuildHost();
        var credentials = new BuildHostCredentials(new SessionOnlyCredentialStore());
        var c = await BuildProviderPlanning.ConnectAsync(Client(host, credentials), CancellationToken.None);
        Assert.Equal(BuildHostConnectionState.NoToken, c.State);
        Assert.Empty(host.Requests); // nothing is sent without a token
        Assert.Contains("hcg-agent build client add executable-maker", c.Message, StringComparison.Ordinal);

        await credentials.SaveAsync("bhc_revoked_token_000000000000");
        Assert.Equal(BuildHostConnectionState.Unauthorized, (await BuildProviderPlanning.ConnectAsync(Client(host, credentials), CancellationToken.None)).State);

        await credentials.SaveAsync(FakeBuildHost.Token);
        c = await BuildProviderPlanning.ConnectAsync(Client(host, credentials), CancellationToken.None);
        Assert.True(c.IsConnected, c.Message);
        Assert.Equal("lifeos", Assert.Single(c.Projects!).Id);
        Assert.True(c.Status!.Enforced);
        Assert.Contains("hcg.buildhost/1", c.Message, StringComparison.Ordinal);

        using var other = new FakeBuildHost { ContractHeader = "hcg.buildhost/2" };
        Assert.Equal(BuildHostConnectionState.ContractMismatch, (await BuildProviderPlanning.ConnectAsync(Client(other, credentials), CancellationToken.None)).State);
    }

    [Fact]
    public async Task Nothing_answering_is_unreachable_not_unauthorized()
    {
        var credentials = new BuildHostCredentials(new SessionOnlyCredentialStore());
        await credentials.SaveAsync(FakeBuildHost.Token);
        var client = new HcgBuildHostClient(HcgBuildHostClient.CreateHttpClient(), credentials, new HcgBuildHostSettings { BaseUrl = new Uri("http://127.0.0.1:9/buildhost/v1/") });
        var c = await BuildProviderPlanning.ConnectAsync(client, CancellationToken.None);
        Assert.Equal(BuildHostConnectionState.Unreachable, c.State);
        Assert.Contains("Start HCG Server", c.Message, StringComparison.Ordinal);
    }

    private sealed class UnreachableKeyring : ISecureCredentialStore
    {
        public string Description => "Secret Service (libsecret keyring)";
        public bool IsPersistent => true;
        public Task<string?> ReadAsync(string account, CancellationToken ct = default) => throw new InvalidOperationException("The keyring could not read the HCG session: Cannot autolaunch D-Bus without X11 $DISPLAY");
        public Task WriteAsync(string account, string secret, CancellationToken ct = default) => throw new InvalidOperationException("The keyring could not store the HCG session: Cannot autolaunch D-Bus without X11 $DISPLAY");
        public Task DeleteAsync(string account, CancellationToken ct = default) => throw new InvalidOperationException("The keyring could not delete the HCG session: no D-Bus");
    }

    [Fact]
    public async Task An_unreachable_keyring_keeps_the_token_for_this_run_only_and_says_so()
    {
        var credentials = new BuildHostCredentials(new UnreachableKeyring());
        Assert.True(credentials.IsPersistent);
        Assert.Null(await credentials.ReadAsync());
        Assert.False(credentials.IsPersistent);
        Assert.StartsWith("this session only (the system keyring could not be reached: Cannot autolaunch D-Bus", credentials.StoreDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("HCG session", credentials.StoreDescription, StringComparison.Ordinal);
        await credentials.SaveAsync(FakeBuildHost.Token);
        Assert.Equal(FakeBuildHost.Token, await credentials.ReadAsync());
        await credentials.ForgetAsync();
        Assert.Null(await credentials.ReadAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => credentials.SaveAsync("short"));
    }

    [Fact]
    public async Task Verified_provenance_is_remembered_without_the_token()
    {
        using var host = new FakeBuildHost();
        var credentials = new BuildHostCredentials(new SessionOnlyCredentialStore());
        await credentials.SaveAsync(FakeBuildHost.Token);
        var client = Client(host, credentials);
        var pv = await client.GetProvenanceAsync(FakeBuildHost.JobId);
        var known = BuildProviderPlanning.Remember(client.BaseUrl, pv, DateTimeOffset.UnixEpoch);
        Assert.Equal(host.Fingerprint, known.KeyFingerprint);
        Assert.Equal(client.BaseUrl.ToString(), known.BaseUrl);
        Assert.DoesNotContain(FakeBuildHost.Token, System.Text.Json.JsonSerializer.Serialize(known), StringComparison.Ordinal);
    }

    [Fact]
    public void The_build_host_feature_is_free_and_decided_by_hcg_server_not_centrally()
    {
        var f = MakerFeatures.BuildHostServer;
        Assert.Equal(FeatureKind.Free, f.Kind);
        Assert.Null(f.CentralKey);
        Assert.Same(f, MakerFeatures.ById("buildhost.local"));
        Assert.Contains(f, MakerFeatures.All);
    }
}
