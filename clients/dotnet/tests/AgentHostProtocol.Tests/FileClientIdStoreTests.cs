// Port of the F-group FileClientIdStore parity tests
// (clients/swift/.../Tests/AgentHostProtocolClientTests/FileClientIdStoreTests.swift).
//
// Exercises the REAL FileClientIdStore against a REAL temp filesystem directory
// — no mocking of System.IO, the store, or the IClientIdStore interface. The
// store's entire contract is real-file persistence, so a real temp dir is the
// only meaningful test surface (mirrors Swift, which uses a real temp dir).
#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AgentHostProtocol.Hosts;
using Xunit;

namespace Microsoft.AgentHostProtocol.Tests;

public sealed class FileClientIdStoreTests : IDisposable
{
    // A unique temp directory per test instance; removed on Dispose. The store
    // itself creates this directory lazily on first write (we don't pre-create
    // it, mirroring Swift's "directory is created on first write" contract).
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "ahp-file-client-id-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── F: round-trip + survives across instances ─────────────────────────────
    // Swift: testStoreAndLoadRoundTrips + testSurvivesAcrossInstances
    // (also folds in testLoadReturnsNilForUnknownHost + testStoreOverwrites).
    [Fact]
    public async Task FileClientIdStore_RoundTripsAndSurvivesInstances()
    {
        var writer = new FileClientIdStore(_tempDir);

        // Unknown host before any write has no stored id.
        Assert.Null(await writer.LoadAsync(new HostId("alpha"), TestContext.Current.CancellationToken));

        // Store then load within the same instance round-trips the value.
        await writer.StoreAsync(new HostId("alpha"), "abc-123", TestContext.Current.CancellationToken);
        Assert.Equal("abc-123", await writer.LoadAsync(new HostId("alpha"), TestContext.Current.CancellationToken));

        // Overwrite: the second store for the same host wins.
        await writer.StoreAsync(new HostId("alpha"), "abc-456", TestContext.Current.CancellationToken);
        Assert.Equal("abc-456", await writer.LoadAsync(new HostId("alpha"), TestContext.Current.CancellationToken));

        // Survives across instances: a fresh store rooted at the SAME directory
        // (simulating a process restart) reads the persisted value back.
        var reader = new FileClientIdStore(_tempDir);
        Assert.Equal("abc-456", await reader.LoadAsync(new HostId("alpha"), TestContext.Current.CancellationToken));
    }

    // ── F: per-host keying ────────────────────────────────────────────────────
    // Swift: testStoresAreKeyedPerHost
    [Fact]
    public async Task FileClientIdStore_KeysPerHost()
    {
        var store = new FileClientIdStore(_tempDir);

        await store.StoreAsync(new HostId("a"), "id-a", TestContext.Current.CancellationToken);
        await store.StoreAsync(new HostId("b"), "id-b", TestContext.Current.CancellationToken);

        // Each host keeps its own value — storing "b" doesn't clobber "a".
        Assert.Equal("id-a", await store.LoadAsync(new HostId("a"), TestContext.Current.CancellationToken));
        Assert.Equal("id-b", await store.LoadAsync(new HostId("b"), TestContext.Current.CancellationToken));
    }

    // ── F: url-unsafe host id is persisted ────────────────────────────────────
    // Swift: testHostIdWithUrlUnsafeCharactersIsPersisted
    [Fact]
    public async Task FileClientIdStore_HandlesUrlUnsafeId()
    {
        var store = new FileClientIdStore(_tempDir);
        // Contains ':' '/' ' ' '?' '=' — none of which are filesystem-safe, so
        // the store must encode them into a stable, safe filename and still
        // round-trip the value.
        var trickyId = new HostId("copilot://tunnel/foo bar?baz=1");

        await store.StoreAsync(trickyId, "tricky-id", TestContext.Current.CancellationToken);

        Assert.Equal("tricky-id", await store.LoadAsync(trickyId, TestContext.Current.CancellationToken));
        // A distinct (but similar) id must not collide with the first.
        var otherId = new HostId("copilot://tunnel/foo bar?baz=2");
        await store.StoreAsync(otherId, "other-id", TestContext.Current.CancellationToken);
        Assert.Equal("other-id", await store.LoadAsync(otherId, TestContext.Current.CancellationToken));
        Assert.Equal("tricky-id", await store.LoadAsync(trickyId, TestContext.Current.CancellationToken));
    }

    // ── F: concurrent writes don't corrupt ────────────────────────────────────
    // Swift: testConcurrentStoresDoNotCorrupt
    [Fact]
    public async Task FileClientIdStore_ConcurrentWrites_NoCorruption()
    {
        var store = new FileClientIdStore(_tempDir);

        // Fan out 32 parallel stores to distinct hosts. Atomic writes + the
        // serialising gate guarantee every write lands intact and none is lost
        // or half-written.
        var writes = new Task[32];
        for (var i = 0; i < writes.Length; i++)
        {
            var n = i;
            writes[n] = Task.Run(() => store.StoreAsync(new HostId($"h-{n}"), $"id-{n}", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        }
        await Task.WhenAll(writes);

        for (var i = 0; i < writes.Length; i++)
        {
            var value = await store.LoadAsync(new HostId($"h-{i}"), TestContext.Current.CancellationToken);
            Assert.Equal($"id-{i}", value);
        }
    }

    // ── F: file is owner-only on Unix ─────────────────────────────────────────
    // Swift: testFileIsRestrictedToOwnerWhenPossible. On non-Unix the perm
    // check is a no-op (the store still ran + persisted), mirroring Swift's
    // "WhenPossible" — the round-trip below proves the write happened either way.
    [Fact]
    public async Task FileClientIdStore_FileIsOwnerOnlyOnUnix()
    {
        var store = new FileClientIdStore(_tempDir);
        await store.StoreAsync(new HostId("h"), "value", TestContext.Current.CancellationToken);

        // The value persisted regardless of platform.
        Assert.Equal("value", await store.LoadAsync(new HostId("h"), TestContext.Current.CancellationToken));

        // On Unix, the persisted file is restricted to owner read/write (0o600).
        if (!OperatingSystem.IsWindows())
        {
            var path = Path.Combine(_tempDir, "h.clientid");
            Assert.True(File.Exists(path), $"expected persisted file at {path}");
            var mode = File.GetUnixFileMode(path);
            // Mask to the permission bits and assert exactly owner read+write.
            var permBits = mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, permBits);
        }
    }

    // ── F: directory path is actually a file ──────────────────────────────────
    // .NET-specific sub-case (Swift's FileClientIdStore swallows directory errors
    // via `try?` in ensureDirectory; the .NET port surfaces them loudly instead).
    // When the configured store directory is an EXISTING regular file,
    // EnsureDirectory() -> Directory.CreateDirectory(<file>) throws IOException;
    // StoreAsync propagates it (the throw happens before the temp-file
    // try/catch). A real temp FILE stands in for the bad "directory" so we
    // exercise the real FS + real store, no mocking.
    [Fact]
    public async Task FileClientIdStore_DirectoryPathIsFile_StoreThrows()
    {
        // Use a sibling path under the test temp root that we create AS A FILE,
        // then point the store at it. (_tempDir itself is cleaned up on Dispose;
        // this file lives inside it so it's cleaned up too.)
        Directory.CreateDirectory(_tempDir);
        var fileAsDir = Path.Combine(_tempDir, "not-a-directory");
        await File.WriteAllTextAsync(fileAsDir, "i am a file, not a directory", TestContext.Current.CancellationToken);

        var store = new FileClientIdStore(fileAsDir);

        // Storing forces EnsureDirectory(), which can't create a directory where
        // a file already exists — the store surfaces this as an IOException
        // rather than silently dropping the write.
        var ex = await Record.ExceptionAsync(
            async () => await store.StoreAsync(new HostId("h"), "value", TestContext.Current.CancellationToken));

        Assert.NotNull(ex);
        Assert.IsAssignableFrom<IOException>(ex);

        // The stand-in file is untouched (the store didn't clobber it with a
        // half-written client-id payload).
        Assert.Equal("i am a file, not a directory", await File.ReadAllTextAsync(fileAsDir, TestContext.Current.CancellationToken));
    }
}
