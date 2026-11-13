using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HCG.ExecutableMaker.Account;

/// <summary>
/// The operating system's credential store: Windows Credential Manager, the macOS Keychain, or the Secret Service
/// (libsecret via <c>secret-tool</c>) on Linux. When none is usable, <see cref="SessionOnlyCredentialStore"/> keeps the
/// session in memory for this run only — it is never written to a plain file.
/// </summary>
public interface ISecureCredentialStore
{
    string Description { get; }
    bool IsPersistent { get; }
    Task<string?> ReadAsync(string account, CancellationToken ct = default);
    Task WriteAsync(string account, string secret, CancellationToken ct = default);
    Task DeleteAsync(string account, CancellationToken ct = default);
}

public static class SecureCredentialStores
{
    public const string Service = "com.hakmicapitalgroup.executablemaker.hcg";

    /// <summary>The platform store, or the session-only store (with the reason) when the platform store is unavailable.</summary>
    public static (ISecureCredentialStore Store, string? Problem) ForCurrentPlatform()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return (new WindowsCredentialStore(Service), null);
            if (OperatingSystem.IsMacOS()) return (new MacKeychainStore(Service), null);
            if (LinuxSecretToolStore.Find() is { } tool) return (new LinuxSecretToolStore(Service, tool), null);
            return (new SessionOnlyCredentialStore(), "No Secret Service (secret-tool) was found, so the HCG session is kept for this run only and you sign in again next time. Install libsecret-tools and a keyring to stay signed in.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return (new SessionOnlyCredentialStore(), $"The system credential store is not available ({ex.Message}); the HCG session is kept for this run only.");
        }
    }
}

/// <summary>In-memory only: survives nothing but this process. Used when no OS store is available, and in tests.</summary>
public sealed class SessionOnlyCredentialStore : ISecureCredentialStore
{
    private readonly Dictionary<string, string> _items = new();
    public string Description => "this session only (not stored)";
    public bool IsPersistent => false;
    public Task<string?> ReadAsync(string account, CancellationToken ct = default) { lock (_items) return Task.FromResult(_items.GetValueOrDefault(account)); }
    public Task WriteAsync(string account, string secret, CancellationToken ct = default) { lock (_items) _items[account] = secret; return Task.CompletedTask; }
    public Task DeleteAsync(string account, CancellationToken ct = default) { lock (_items) _items.Remove(account); return Task.CompletedTask; }
}

/// <summary>Windows Credential Manager (generic credential, local machine persistence, current user only).</summary>
public sealed class WindowsCredentialStore(string service) : ISecureCredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    public const int MaxBlobBytes = 2560;

    public string Description => "Windows Credential Manager";
    public bool IsPersistent => true;
    private string Target(string account) => $"{service}:{account}";

    public Task<string?> ReadAsync(string account, CancellationToken ct = default)
    {
        if (!CredReadW(Target(account), CredTypeGeneric, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound) return Task.FromResult<string?>(null);
            throw new Win32Exception(err, "Windows Credential Manager could not read the HCG session.");
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        finally { CredFree(ptr); }
    }

    public Task WriteAsync(string account, string secret, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > MaxBlobBytes) throw new InvalidOperationException("The credential is larger than Windows Credential Manager allows.");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric, TargetName = Target(account), CredentialBlobSize = bytes.Length, CredentialBlob = blob,
                Persist = CredPersistLocalMachine, UserName = account, Comment = "HCG Executable Maker — HCG sign-in",
            };
            if (!CredWriteW(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not store the HCG session.");
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length); // wipe the unmanaged copy
            Marshal.FreeHGlobal(blob);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string account, CancellationToken ct = default)
    {
        if (!CredDeleteW(Target(account), CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not delete the HCG session.");
        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>macOS Keychain (generic password item in the user's default keychain).</summary>
public sealed class MacKeychainStore(string service) : ISecureCredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;

    public string Description => "macOS Keychain";
    public bool IsPersistent => true;

    public Task<string?> ReadAsync(string account, CancellationToken ct = default)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(account);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out var length, out var data, out var item);
        if (status == ErrSecItemNotFound) return Task.FromResult<string?>(null);
        Check(status, "read");
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public Task WriteAsync(string account, string secret, CancellationToken ct = default)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(account);
        var p = Encoding.UTF8.GetBytes(secret);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
        if (status == 0)
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            try { Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)p.Length, p), "update"); }
            finally { CFRelease(item); }
        }
        else if (status == ErrSecItemNotFound)
        {
            Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, (uint)p.Length, p, out var added), "store");
            if (added != IntPtr.Zero) CFRelease(added);
        }
        else Check(status, "read");
        Array.Clear(p);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string account, CancellationToken ct = default)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(account);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
        if (status == ErrSecItemNotFound) return Task.CompletedTask;
        Check(status, "read");
        SecKeychainItemFreeContent(IntPtr.Zero, data);
        try { Check(SecKeychainItemDelete(item), "delete"); }
        finally { CFRelease(item); }
        return Task.CompletedTask;
    }

    private static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"The macOS Keychain could not {what} the HCG session (OSStatus {status}).");
    }

    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);
    [DllImport(Security)] private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr itemRef);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
}

/// <summary>Linux Secret Service through <c>secret-tool</c> (libsecret). The secret is passed on stdin, never on the command line.</summary>
public sealed class LinuxSecretToolStore(string service, string tool) : ISecureCredentialStore
{
    public string Description => "Secret Service (libsecret keyring)";
    public bool IsPersistent => true;

    public static string? Find() =>
        new[] { "/usr/bin/secret-tool", "/usr/local/bin/secret-tool", "/bin/secret-tool" }.FirstOrDefault(File.Exists);

    public async Task<string?> ReadAsync(string account, CancellationToken ct = default)
    {
        var (code, stdout, stderr) = await RunAsync(["lookup", "service", service, "account", account], null, ct).ConfigureAwait(false);
        if (code == 0) return stdout.Length == 0 ? null : stdout;
        if (code == 1 && stderr.Trim().Length == 0) return null; // not found
        throw new InvalidOperationException($"The keyring could not read the HCG session: {Clean(stderr)}");
    }

    public async Task WriteAsync(string account, string secret, CancellationToken ct = default)
    {
        var (code, _, stderr) = await RunAsync(["store", "--label=HCG Executable Maker - HCG sign-in", "service", service, "account", account], secret, ct).ConfigureAwait(false);
        if (code != 0) throw new InvalidOperationException($"The keyring could not store the HCG session: {Clean(stderr)}");
    }

    public async Task DeleteAsync(string account, CancellationToken ct = default)
    {
        var (code, _, stderr) = await RunAsync(["clear", "service", service, "account", account], null, ct).ConfigureAwait(false);
        if (code is not (0 or 1)) throw new InvalidOperationException($"The keyring could not delete the HCG session: {Clean(stderr)}");
    }

    /// <summary>secret-tool's own message without its "secret-tool:" prefix (which reads like a key/value secret to the redactor).</summary>
    private static string Clean(string stderr)
    {
        var line = stderr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return (line.StartsWith("secret-tool:", StringComparison.Ordinal) ? line["secret-tool:".Length..] : line).Trim();
    }

    private async Task<(int Code, string StdOut, string StdErr)> RunAsync(string[] args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("secret-tool could not be started.");
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        return (p.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }
}
