// Filesystem-backed IClientIdStore that survives process restarts.
// Faithful port of clients/swift/.../Hosts/ClientIdStore.swift (FileClientIdStore).
//
// One file per host id under a configurable directory; writes are atomic
// (temp file + File.Move overwrite, atomic on the same volume) and best-effort
// restrict permissions to owner-read/write on Unix so the persisted ids aren't
// world-readable. Per-store mutations are serialised through a SemaphoreSlim
// (mirroring Swift's `actor Storage`) so concurrent load/store calls from
// different hosts don't race on the directory's contents.
#nullable enable

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>
/// Filesystem-backed <see cref="IClientIdStore"/> that survives process
/// restarts. Stores one <c>&lt;encoded-host-id&gt;.clientid</c> file per host
/// under <see cref="Directory"/>; writes are atomic and best-effort restricted
/// to owner-only permissions on Unix. Mirrors Swift's <c>FileClientIdStore</c>.
/// </summary>
/// <remarks>
/// For the highest-security profile on Apple platforms, wrap a keychain-backed
/// implementation of <see cref="IClientIdStore"/> instead — this store is a
/// reasonable default for desktops, command-line tools, and development builds:
/// it provides persistence without depending on a platform secret store.
/// The directory is created on first write if it doesn't already exist;
/// filenames are derived from each host id via a percent-encoding helper so
/// arbitrary <see cref="HostId"/> strings (including <c>:</c>, <c>/</c>, etc.)
/// map to safe filesystem paths.
/// </remarks>
public sealed class FileClientIdStore : IClientIdStore, IDisposable
{
    // Serialises mutations across hosts (mirrors Swift's `actor Storage`).
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The directory this store persists client-id files under.</summary>
    public string Directory { get; }

    /// <summary>
    /// Builds a store rooted at <paramref name="directory"/>. The directory is
    /// created when needed; the caller is responsible for picking a location
    /// the process can write to (e.g. an application-support directory on
    /// desktop platforms, <c>XDG_DATA_HOME</c> / <c>~/.local/share</c> on Linux).
    /// </summary>
    public FileClientIdStore(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Directory = directory;
    }

    /// <inheritdoc />
    public async Task<string?> LoadAsync(HostId host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = FilePath(host);
            string text;
            try
            {
                // Read the bytes ourselves + decode UTF-8 to mirror Swift's
                // Data(contentsOf:) + String(data:encoding:.utf8). A missing
                // file (never stored) yields null, not an error.
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }

            var trimmed = text.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StoreAsync(HostId host, string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(clientId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectory();
            var path = FilePath(host);
            var bytes = Encoding.UTF8.GetBytes(clientId);

            // Atomic write: write to a unique temp file in the same directory,
            // then File.Move(overwrite) — atomic on the same volume — so a
            // concurrent reader never observes a half-written file (mirrors
            // Swift's `.atomic` Data write option).
            var tempPath = Path.Combine(Directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
                // Set owner-only perms on the temp file BEFORE the move so the
                // destination is never momentarily world-readable.
                TrySetOwnerOnlyFile(tempPath);
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                // Best-effort cleanup of the temp file on any failure so we
                // don't leak partial writes into the directory.
                TryDelete(tempPath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases the mutation semaphore. A store owning a <see cref="SemaphoreSlim"/>
    /// is disposable per the .NET convention; callers creating a store per
    /// short-lived operation should dispose it. Safe to call multiple times.
    /// </summary>
    public void Dispose() => _gate.Dispose();

    private void EnsureDirectory()
    {
        if (System.IO.Directory.Exists(Directory)) return;
        System.IO.Directory.CreateDirectory(Directory);
        // Best-effort restrict the directory to owner-only on Unix (0o700).
        TrySetOwnerOnlyDirectory(Directory);
    }

    private string FilePath(HostId host) => Path.Combine(Directory, Encode(host) + ".clientid");

    /// <summary>
    /// Percent-encodes a host id into a safe, stable filename component. Reuses
    /// the same RFC-3986 unreserved-passthrough encoding as
    /// <see cref="HostedResourceKey.PercentEscape"/> (ALPHA / DIGIT / -._~ pass
    /// through, everything else becomes <c>%XX</c>), mirroring Swift's
    /// <c>addingPercentEncoding(withAllowedCharacters:)</c> over
    /// <c>alphanumerics + "-._~"</c>. The reverse direction isn't needed because
    /// we only read files we wrote, by the same key.
    /// </summary>
    private static string Encode(HostId host) => HostedResourceKey.PercentEscape(host.ToString());

    // ── Best-effort owner-only permissions (no-op off Unix) ───────────────────

    private static void TrySetOwnerOnlyFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* best-effort: ignore on platforms/filesystems that reject it */ }
        }
    }

    private static void TrySetOwnerOnlyDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { /* best-effort */ }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
