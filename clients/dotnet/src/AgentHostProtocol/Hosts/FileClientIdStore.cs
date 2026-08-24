// Filesystem-backed IClientIdStore that survives process restarts.
// Faithful port of clients/swift/.../Hosts/ClientIdStore.swift (FileClientIdStore).
//
// One file per host id under a configurable directory; writes are atomic
// (temp file + File.Move overwrite, atomic on the same volume) and establish
// owner-read/write permissions before any bytes are written on Unix. Per-store
// mutations are serialised through a SemaphoreSlim
// (mirroring Swift's `actor Storage`) so concurrent load/store calls from
// different hosts don't race on the directory's contents.
#nullable enable

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.AgentHostProtocol.Hosts;

/// <summary>
/// Filesystem-backed <see cref="IClientIdStore"/> that survives process
/// restarts. Stores one <c>&lt;encoded-host-id&gt;.clientid</c> file per host
/// under <see cref="Directory"/>; writes are atomic and restricted to owner-only
/// permissions on Unix. Mirrors Swift's <c>FileClientIdStore</c>.
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
/// On Unix, a write fails before persisting any client-ID bytes if owner-only
/// permissions cannot be established on the temporary file.
/// </remarks>
public sealed class FileClientIdStore : IClientIdStore, IDisposable
{
    // Serialises mutations across hosts (mirrors Swift's `actor Storage`).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string>? _tempFileReadyForWrite;
    private readonly bool _useNativeUnixFileCreation;

    /// <summary>The directory this store persists client-id files under.</summary>
    public string Directory { get; }

    /// <summary>
    /// Builds a store rooted at <paramref name="directory"/>. The directory is
    /// created when needed; the caller is responsible for picking a location
    /// the process can write to (e.g. an application-support directory on
    /// desktop platforms, <c>XDG_DATA_HOME</c> / <c>~/.local/share</c> on Linux).
    /// </summary>
    public FileClientIdStore(string directory)
        : this(directory, tempFileReadyForWrite: null)
    {
    }

    internal FileClientIdStore(
        string directory,
        Action<string>? tempFileReadyForWrite,
        bool useNativeUnixFileCreation = false)
    {
        Guard.ThrowIfNull(directory, nameof(directory));
        Directory = directory;
        _tempFileReadyForWrite = tempFileReadyForWrite;
#if NET8_0_OR_GREATER
        _useNativeUnixFileCreation = useNativeUnixFileCreation;
#else
        _useNativeUnixFileCreation = true;
#endif
    }

    /// <inheritdoc />
    public async Task<string?> LoadAsync(HostId host, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(host, nameof(host));
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
                byte[] bytes;
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true))
                using (var buffer = new MemoryStream())
                {
                    await stream.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
                    bytes = buffer.ToArray();
                }
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
        Guard.ThrowIfNull(host, nameof(host));
        Guard.ThrowIfNull(clientId, nameof(clientId));
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
                using (var stream = CreateTempFile(tempPath))
                {
                    _tempFileReadyForWrite?.Invoke(tempPath);
#if NETSTANDARD2_0
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
#else
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
#endif
                }
#if NETSTANDARD2_0
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
#else
                File.Move(tempPath, path, overwrite: true);
#endif
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

    private FileStream CreateTempFile(string path)
    {
#if NET8_0_OR_GREATER
        if (!OperatingSystem.IsWindows())
        {
            if (_useNativeUnixFileCreation)
            {
                return CreateSecureUnixTempFile(path);
            }

            var stream = new FileStream(path, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            try
            {
                // UnixCreateMode applies 0600 atomically at creation. Normalize
                // the exact mode before exposing the stream to any write.
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
#else
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return _useNativeUnixFileCreation
                ? CreateSecureUnixTempFile(path)
                : throw new InvalidOperationException("Native Unix file creation must be enabled for netstandard2.0.");
        }
#endif

        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
    }

    // ── Best-effort owner-only directory permissions (no-op off Unix) ─────────

    private static void TrySetOwnerOnlyDirectory(string path)
    {
#if NET8_0_OR_GREATER
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
#else
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { ChmodUtf8(path, Convert.ToUInt32("700", 8)); }
            catch { /* best-effort */ }
        }
#endif
    }

    [DllImport("libc", EntryPoint = "open", ExactSpelling = true, SetLastError = true)]
    private static extern int Open(IntPtr path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "fchmod", ExactSpelling = true, SetLastError = true)]
    private static extern int Fchmod(int fileDescriptor, uint mode);

#if NETSTANDARD2_0
    [DllImport("libc", EntryPoint = "chmod", ExactSpelling = true, SetLastError = true)]
    private static extern int Chmod(IntPtr path, uint mode);
#endif

    private static FileStream CreateSecureUnixTempFile(string path)
    {
        const uint OwnerReadWrite = 0x180; // 0600
        const int WriteOnly = 0x0001;
        const int LinuxCreateExclusive = 0x0040 | 0x0080;
        const int BsdCreateExclusive = 0x0200 | 0x0800;
        const int LinuxCloseOnExec = 0x00080000;
        const int MacOsCloseOnExec = 0x01000000;
        const int FreeBsdCloseOnExec = 0x00100000;

        int flags;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            flags = WriteOnly | LinuxCreateExclusive | LinuxCloseOnExec;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            flags = WriteOnly | BsdCreateExclusive | MacOsCloseOnExec;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD")))
        {
            flags = WriteOnly | BsdCreateExclusive | FreeBsdCloseOnExec;
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Secure client ID storage requires a known atomic O_CLOEXEC value on this Unix platform.");
        }

        int fileDescriptor = WithUtf8Path(
            path,
            nativePath => Open(nativePath, flags, OwnerReadWrite));
        if (fileDescriptor < 0)
        {
            throw CreateUnixIOException("create secure temporary file", path);
        }

#pragma warning disable CA2000 // Ownership transfers to the returned FileStream.
        var handle = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
#pragma warning restore CA2000
        try
        {
            if (Fchmod(fileDescriptor, OwnerReadWrite) != 0)
            {
                throw CreateUnixIOException("set owner-only permissions on temporary file", path);
            }

            // open(2) returns a synchronous descriptor. FileStream still supports
            // WriteAsync on it, but must not be told the handle uses overlapped I/O.
            return new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

#if NETSTANDARD2_0
    private static void ChmodUtf8(string path, uint mode)
    {
        _ = WithUtf8Path(path, nativePath => Chmod(nativePath, mode));
    }
#endif

    private static T WithUtf8Path<T>(string path, Func<IntPtr, T> action)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path + "\0");
        IntPtr nativePath = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, nativePath, bytes.Length);
            return action(nativePath);
        }
        finally
        {
            Marshal.FreeHGlobal(nativePath);
        }
    }

    private static IOException CreateUnixIOException(string operation, string path)
    {
        int error = Marshal.GetLastWin32Error();
        return new IOException(
            $"Failed to {operation} '{path}'.",
            new Win32Exception(error));
    }
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
