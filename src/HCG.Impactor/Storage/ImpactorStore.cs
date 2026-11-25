using System.Text.Json;
using System.Text.Json.Serialization;

namespace HCG.Impactor.Storage;

public enum InstallOutcome { Installed, Failed, Cancelled, Blocked }

/// <summary>One installation attempt (also the Deployment Queue's finished items). No secrets; the UDID is stored redacted.</summary>
public sealed record InstallRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset At { get; init; }
    public required string AppName { get; init; }
    public string? BundleIdentifier { get; init; }
    public string? Version { get; init; }
    public string? IpaPath { get; init; }
    public string? IpaSha256 { get; init; }
    public required string DeviceName { get; init; }
    public string? DeviceModel { get; init; }
    public string? DeviceOs { get; init; }
    /// <summary>CoreDevice identifier (UUID) — identifies the device on this Mac only.</summary>
    public string? DeviceIdentifier { get; init; }
    public string? DeviceUdidRedacted { get; init; }
    public string? Connection { get; init; }
    public InstallOutcome Outcome { get; init; }
    public string? DiagnosisKind { get; init; }
    public string? Summary { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Launched { get; init; }
}

/// <summary>An app HCG signed: what Signing Monitor tracks for expiry reminders and re-signing.</summary>
public sealed record SignedAppRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset SignedAt { get; init; }
    public required string AppName { get; init; }
    public required string BundleIdentifier { get; init; }
    public string? Version { get; init; }
    public string? Build { get; init; }
    public string? SourceIpa { get; init; }
    public string? SignedIpa { get; init; }
    public string? SignedSha256 { get; init; }
    public string? IdentityName { get; init; }
    public string? IdentitySha1 { get; init; }
    public DateTimeOffset? IdentityExpires { get; init; }
    public string? ProfileName { get; init; }
    public string? ProfileUuid { get; init; }
    public DateTimeOffset? ProfileExpires { get; init; }
    public string? Method { get; init; }
    public string? TeamId { get; init; }
    /// <summary>The earliest of certificate and profile expiry: the app stops launching after this.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastInstalledAt { get; init; }
    public IReadOnlyList<string> InstalledOn { get; init; } = [];
}

/// <summary>Reminder for Signing Monitor.</summary>
public sealed record ExpiryReminder(SignedAppRecord App, string Level, string Message);

/// <summary>
/// HCG Impactor's local records: signed apps and installation history, as JSON in the app data folder
/// (<c>impactor/</c>). Atomic writes; corrupt files are kept aside and replaced, never silently merged.
/// </summary>
public sealed class ImpactorStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _dir;
    private readonly object _gate = new();
    public const int MaxHistory = 2000;

    public ImpactorStore(string dataRoot)
    {
        _dir = Path.Combine(dataRoot, "impactor");
    }

    public string Directory => _dir;
    private string HistoryPath => Path.Combine(_dir, "install-history.json");
    private string AppsPath => Path.Combine(_dir, "signed-apps.json");

    public IReadOnlyList<InstallRecord> History() { lock (_gate) return Load<List<InstallRecord>>(HistoryPath).OrderByDescending(r => r.At).ToList(); }
    public IReadOnlyList<SignedAppRecord> SignedApps() { lock (_gate) return Load<List<SignedAppRecord>>(AppsPath).OrderByDescending(a => a.SignedAt).ToList(); }

    public void Add(InstallRecord record)
    {
        lock (_gate)
        {
            var list = Load<List<InstallRecord>>(HistoryPath);
            list.Add(record);
            Save(HistoryPath, list.OrderByDescending(r => r.At).Take(MaxHistory).ToList());
            if (record.Outcome == InstallOutcome.Installed && record.IpaSha256 is { } sha)
            {
                var apps = Load<List<SignedAppRecord>>(AppsPath);
                var i = apps.FindIndex(a => a.SignedSha256 == sha);
                if (i >= 0)
                {
                    apps[i] = apps[i] with { LastInstalledAt = record.At, InstalledOn = apps[i].InstalledOn.Append(record.DeviceName).Distinct().ToList() };
                    Save(AppsPath, apps);
                }
            }
        }
    }

    public void Add(SignedAppRecord app)
    {
        lock (_gate)
        {
            var list = Load<List<SignedAppRecord>>(AppsPath);
            list.RemoveAll(a => a.SignedSha256 is not null && a.SignedSha256 == app.SignedSha256);
            list.Add(app);
            Save(AppsPath, list);
        }
    }

    public void Remove(Guid signedAppId)
    {
        lock (_gate)
        {
            var list = Load<List<SignedAppRecord>>(AppsPath);
            if (list.RemoveAll(a => a.Id == signedAppId) > 0) Save(AppsPath, list);
        }
    }

    /// <summary>Reminders for apps whose signature expires within <paramref name="days"/> days (or already expired).</summary>
    public IReadOnlyList<ExpiryReminder> Reminders(DateTimeOffset now, int days = 14) =>
        SignedApps().Where(a => a.ExpiresAt is not null && a.ExpiresAt - now < TimeSpan.FromDays(days))
            .GroupBy(a => a.BundleIdentifier).Select(g => g.OrderByDescending(a => a.ExpiresAt).First())
            .Where(a => a.ExpiresAt - now < TimeSpan.FromDays(days))
            .Select(a => a.ExpiresAt <= now
                ? new ExpiryReminder(a, "expired", $"{a.AppName}'s signature expired on {a.ExpiresAt:yyyy-MM-dd}; it no longer launches. Re-sign and reinstall it.")
                : new ExpiryReminder(a, "soon", $"{a.AppName}'s signature expires in {(int)Math.Ceiling((a.ExpiresAt!.Value - now).TotalDays)} day(s) ({a.ExpiresAt:yyyy-MM-dd}). Re-sign and reinstall before then."))
            .OrderBy(r => r.App.ExpiresAt).ToList();

    private T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T(); }
        catch (JsonException)
        {
            try { File.Move(path, path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false); } catch (IOException) { }
            return new T();
        }
    }

    private void Save<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(_dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
