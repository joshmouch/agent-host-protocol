//! Persistence hooks for stable per-host `clientId`s.
//!
//! Each host in [`super::MultiHostClient`] needs a stable `clientId`
//! so the AHP `reconnect` flow can replay missed actions on the next
//! launch. By default [`super::MultiHostClient`] is wired with an
//! [`InMemoryClientIdStore`] — session-stable but not durable. For
//! cross-launch identity (which the reconnect flow needs to be
//! useful across process restarts), supply a persistent
//! implementation via [`super::MultiHostClient::with_client_id_store`]:
//! [`FileClientIdStore`] is shipped here; consumers wanting a
//! keychain/secure-enclave backing should implement [`ClientIdStore`]
//! themselves.

use std::collections::HashMap;
use std::future::Future;
use std::path::{Path, PathBuf};
use std::pin::Pin;

use thiserror::Error;
use tokio::sync::Mutex as AsyncMutex;

use super::types::HostId;

/// Errors returned by a [`ClientIdStore`].
///
/// The trait surfaces I/O failures rather than swallowing them so
/// callers can tell the difference between "no stored id yet" and
/// "we tried to read it but the disk is full" — without that
/// distinction, a flaky filesystem silently degrades to "fresh
/// `clientId` on every launch" and the reconnect flow stops working
/// without warning.
#[derive(Debug, Error)]
#[non_exhaustive]
pub enum ClientIdStoreError {
    /// Underlying I/O failure (filesystem, OS keychain, …).
    #[error("client id store io error: {0}")]
    Io(String),

    /// The store rejected the host id (e.g. illegal characters that
    /// the backing store can't escape).
    #[error("client id store rejected host id {host}: {reason}")]
    InvalidHostId {
        /// Host id the store could not accept.
        host: HostId,
        /// Human-readable reason from the implementation.
        reason: String,
    },
}

/// Persistence hook for stable `clientId`s per host.
///
/// [`super::MultiHostClient`] consults this store on every
/// [`super::MultiHostClient::add_host`]: if the store returns
/// `Some(id)`, that id is reused (so the server treats the new
/// connection as the same client and the AHP `reconnect` flow can
/// replay missed actions); on `None`, the SDK generates a fresh id
/// and writes it back through [`ClientIdStore::store`].
///
/// Implementations must be `Send + Sync + 'static` so they can be
/// shared across the multi-host facade and the per-host runtimes.
/// Both methods take an immutable receiver — internal mutability
/// (e.g. a mutex) is the implementation's responsibility.
///
/// # Implementing
///
/// The methods return `Pin<Box<dyn Future<...>>>` rather than
/// `async fn` so the trait is dyn-compatible (the SDK holds a
/// `Box<dyn ClientIdStore>`). This matches the
/// [`super::HostTransportFactory`] pattern used elsewhere in the
/// crate; implementations can be written with `Box::pin(async move
/// { ... })`.
///
/// ```
/// use ahp::hosts::{ClientIdStore, ClientIdStoreError, HostId};
/// use std::pin::Pin;
/// use std::future::Future;
///
/// struct Noop;
///
/// impl ClientIdStore for Noop {
///     fn load(
///         &self,
///         _host_id: &HostId,
///     ) -> Pin<Box<dyn Future<Output = Result<Option<String>, ClientIdStoreError>> + Send + '_>> {
///         Box::pin(async { Ok(None) })
///     }
///
///     fn store(
///         &self,
///         _host_id: &HostId,
///         _client_id: &str,
///     ) -> Pin<Box<dyn Future<Output = Result<(), ClientIdStoreError>> + Send + '_>> {
///         Box::pin(async { Ok(()) })
///     }
/// }
/// ```
pub trait ClientIdStore: Send + Sync + 'static {
    /// Look up the stored `clientId` for `host_id`, if any.
    ///
    /// Returns `Ok(None)` when the store has no entry for that host;
    /// `Err(_)` when the lookup itself failed (e.g. I/O error).
    fn load(
        &self,
        host_id: &HostId,
    ) -> Pin<Box<dyn Future<Output = Result<Option<String>, ClientIdStoreError>> + Send + '_>>;

    /// Persist `client_id` for `host_id`, overwriting any previous
    /// value.
    fn store(
        &self,
        host_id: &HostId,
        client_id: &str,
    ) -> Pin<Box<dyn Future<Output = Result<(), ClientIdStoreError>> + Send + '_>>;
}

/// In-process [`ClientIdStore`] backed by an in-memory map.
///
/// Session-stable: the assigned id survives reconnects within the same
/// process but is lost on restart. Fine for tests, ephemeral CLIs,
/// and as a starting point. Production apps that want reconnect to
/// keep working across launches should swap in [`FileClientIdStore`]
/// (or a keychain-backed implementation of their own).
#[derive(Default)]
pub struct InMemoryClientIdStore {
    entries: AsyncMutex<HashMap<HostId, String>>,
}

impl InMemoryClientIdStore {
    /// Build an empty in-memory store.
    pub fn new() -> Self {
        Self::default()
    }
}

impl std::fmt::Debug for InMemoryClientIdStore {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("InMemoryClientIdStore")
            .finish_non_exhaustive()
    }
}

impl ClientIdStore for InMemoryClientIdStore {
    fn load(
        &self,
        host_id: &HostId,
    ) -> Pin<Box<dyn Future<Output = Result<Option<String>, ClientIdStoreError>> + Send + '_>> {
        let host_id = host_id.clone();
        Box::pin(async move {
            let entries = self.entries.lock().await;
            Ok(entries.get(&host_id).cloned())
        })
    }

    fn store(
        &self,
        host_id: &HostId,
        client_id: &str,
    ) -> Pin<Box<dyn Future<Output = Result<(), ClientIdStoreError>> + Send + '_>> {
        let host_id = host_id.clone();
        let client_id = client_id.to_owned();
        Box::pin(async move {
            let mut entries = self.entries.lock().await;
            entries.insert(host_id, client_id);
            Ok(())
        })
    }
}

/// Filesystem-backed [`ClientIdStore`] that survives process
/// restarts.
///
/// Stores one file per host id under a configurable directory.
/// Writes go through a unique `<file>.<pid>.<counter>.tmp` staging
/// path followed by an atomic-replace rename on Unix; on Windows the
/// rename is best-effort (a previous file is removed first if it
/// exists, with a small race window). Files are restricted to owner
/// read/write (`0o600`) and the directory to owner read/write/exec
/// (`0o700`) on POSIX platforms; these calls are no-ops on Windows.
///
/// Filenames are derived from each host id via percent-encoding so
/// arbitrary characters in [`HostId`] (`:`, `/`, spaces, …) map to
/// safe filesystem paths. The reverse direction isn't needed — only
/// the writer reads its own files, by the same key.
///
/// **iOS Keychain / OS keyring note:** for the strongest-security
/// profile (Keychain on Apple platforms, libsecret on Linux, DPAPI
/// on Windows) implement [`ClientIdStore`] yourself in your app.
/// [`FileClientIdStore`] is a reasonable default for desktop apps,
/// command-line tools, and development builds that don't want to
/// take on a platform-secrets dependency.
pub struct FileClientIdStore {
    directory: PathBuf,
    /// Per-process monotonic counter that disambiguates concurrent
    /// store calls so two writers for the same host id stage to
    /// distinct `.tmp` files. The final `rename` is the only point
    /// of contention; on Unix it's atomic-replace, on Windows it's
    /// remove-then-rename.
    counter: std::sync::atomic::AtomicU64,
}

impl FileClientIdStore {
    /// Build a store rooted at `directory`.
    ///
    /// The directory is created lazily on first write if missing.
    /// The caller is responsible for picking a writable location
    /// (e.g. `dirs::data_dir().unwrap().join("my-app")` on Apple/Linux,
    /// `%LOCALAPPDATA%\my-app` on Windows).
    pub fn new(directory: impl Into<PathBuf>) -> Self {
        Self {
            directory: directory.into(),
            counter: std::sync::atomic::AtomicU64::new(0),
        }
    }

    /// Path the store reads/writes for `host_id`. Public for tests
    /// and for callers that want to back up / migrate the file.
    pub fn path_for(&self, host_id: &HostId) -> PathBuf {
        self.directory
            .join(format!("{}.clientid", encode_host_id(host_id)))
    }
}

impl std::fmt::Debug for FileClientIdStore {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FileClientIdStore")
            .field("directory", &self.directory)
            .finish_non_exhaustive()
    }
}

impl ClientIdStore for FileClientIdStore {
    fn load(
        &self,
        host_id: &HostId,
    ) -> Pin<Box<dyn Future<Output = Result<Option<String>, ClientIdStoreError>> + Send + '_>> {
        let path = self.path_for(host_id);
        Box::pin(async move {
            // Run the blocking I/O off the async runtime. On Windows
            // (remove+rename fallback) a concurrent reader may
            // transiently observe a `NotFound` if it interleaves
            // between the remove and the rename — that surfaces as
            // `Ok(None)` and the caller treats it as "no stored id
            // yet", which is acceptable because the next write will
            // re-establish the file.
            let result = tokio::task::spawn_blocking(move || load_blocking(&path))
                .await
                .map_err(|e| ClientIdStoreError::Io(format!("spawn_blocking join: {e}")))?;
            result
        })
    }

    fn store(
        &self,
        host_id: &HostId,
        client_id: &str,
    ) -> Pin<Box<dyn Future<Output = Result<(), ClientIdStoreError>> + Send + '_>> {
        let path = self.path_for(host_id);
        let directory = self.directory.clone();
        let client_id = client_id.to_owned();
        // Unique-per-call tmp path so two concurrent writers for the
        // same host id don't clobber each other's staging file. The
        // process id + a monotonic counter is enough on every
        // platform; the actual `rename` is still the single point of
        // contention.
        let suffix = format!(
            "{}.{}.tmp",
            std::process::id(),
            self.counter
                .fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        );
        let tmp_path = path.with_extension(format!("clientid.{suffix}"));
        Box::pin(async move {
            tokio::task::spawn_blocking(move || {
                store_blocking(&directory, &path, &tmp_path, &client_id)
            })
            .await
            .map_err(|e| ClientIdStoreError::Io(format!("spawn_blocking join: {e}")))?
        })
    }
}

fn load_blocking(path: &Path) -> Result<Option<String>, ClientIdStoreError> {
    match std::fs::read_to_string(path) {
        Ok(s) => {
            let trimmed = s.trim();
            if trimmed.is_empty() {
                Ok(None)
            } else {
                Ok(Some(trimmed.to_owned()))
            }
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(ClientIdStoreError::Io(format!(
            "read {}: {e}",
            path.display()
        ))),
    }
}

fn store_blocking(
    directory: &Path,
    path: &Path,
    tmp_path: &Path,
    client_id: &str,
) -> Result<(), ClientIdStoreError> {
    ensure_directory(directory)?;
    // Write the payload + perms to the temp file, then rename. On
    // Unix `rename` is atomic-replace; on Windows it errors when
    // the destination already exists, so we fall back to
    // `remove_file + rename` there. The race window on Windows is
    // limited to the single brief instant between the two calls.
    std::fs::write(tmp_path, client_id)
        .map_err(|e| ClientIdStoreError::Io(format!("write {}: {e}", tmp_path.display())))?;
    restrict_file_perms(tmp_path);

    match std::fs::rename(tmp_path, path) {
        Ok(()) => Ok(()),
        Err(_) if cfg!(windows) => {
            // Best-effort: try removing the destination and retrying.
            // If this fails too, surface the rename error.
            let _ = std::fs::remove_file(path);
            std::fs::rename(tmp_path, path).map_err(|e| {
                ClientIdStoreError::Io(format!(
                    "rename {} -> {}: {e}",
                    tmp_path.display(),
                    path.display()
                ))
            })
        }
        Err(e) => {
            let _ = std::fs::remove_file(tmp_path);
            Err(ClientIdStoreError::Io(format!(
                "rename {} -> {}: {e}",
                tmp_path.display(),
                path.display()
            )))
        }
    }
}

fn ensure_directory(directory: &Path) -> Result<(), ClientIdStoreError> {
    if directory.exists() {
        return Ok(());
    }
    std::fs::create_dir_all(directory).map_err(|e| {
        ClientIdStoreError::Io(format!("create_dir_all {}: {e}", directory.display()))
    })?;
    restrict_dir_perms(directory);
    Ok(())
}

#[cfg(unix)]
fn restrict_file_perms(path: &Path) {
    use std::os::unix::fs::PermissionsExt;
    let _ = std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600));
}

#[cfg(not(unix))]
fn restrict_file_perms(_path: &Path) {
    // No-op on non-Unix platforms; callers picking a per-user
    // directory (e.g. AppData on Windows) get OS-level ACL
    // restrictions for free.
}

#[cfg(unix)]
fn restrict_dir_perms(path: &Path) {
    use std::os::unix::fs::PermissionsExt;
    let _ = std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o700));
}

#[cfg(not(unix))]
fn restrict_dir_perms(_path: &Path) {}

/// Percent-encode characters that aren't safe in filesystem paths.
///
/// Allowed set is the unreserved-URL-character set (`A-Z`, `a-z`,
/// `0-9`, `-`, `_`, `.`, `~`); everything else is encoded as `%XX`.
/// Cheap reimplementation rather than pulling in `percent-encoding`
/// — the crate is deliberately dependency-light.
fn encode_host_id(host_id: &HostId) -> String {
    let mut out = String::with_capacity(host_id.as_str().len());
    for byte in host_id.as_str().as_bytes() {
        let c = *byte;
        let unreserved =
            c.is_ascii_alphanumeric() || c == b'-' || c == b'_' || c == b'.' || c == b'~';
        if unreserved {
            out.push(c as char);
        } else {
            out.push('%');
            out.push_str(&format!("{c:02X}"));
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir() -> tempfile::TempDir {
        tempfile::tempdir().expect("create temp dir")
    }

    #[tokio::test]
    async fn in_memory_load_returns_none_for_unknown_host() {
        let store = InMemoryClientIdStore::new();
        let value = store.load(&HostId::new("missing")).await.unwrap();
        assert_eq!(value, None);
    }

    #[tokio::test]
    async fn in_memory_round_trip() {
        let store = InMemoryClientIdStore::new();
        store.store(&HostId::new("alpha"), "abc-123").await.unwrap();
        let value = store.load(&HostId::new("alpha")).await.unwrap();
        assert_eq!(value.as_deref(), Some("abc-123"));
    }

    #[tokio::test]
    async fn in_memory_overwrite_keeps_most_recent_value() {
        let store = InMemoryClientIdStore::new();
        store.store(&HostId::new("h"), "first").await.unwrap();
        store.store(&HostId::new("h"), "second").await.unwrap();
        let value = store.load(&HostId::new("h")).await.unwrap();
        assert_eq!(value.as_deref(), Some("second"));
    }

    #[tokio::test]
    async fn file_load_returns_none_for_unknown_host() {
        let dir = temp_dir();
        let store = FileClientIdStore::new(dir.path());
        let value = store.load(&HostId::new("missing")).await.unwrap();
        assert_eq!(value, None);
    }

    #[tokio::test]
    async fn file_round_trip() {
        let dir = temp_dir();
        let store = FileClientIdStore::new(dir.path());
        store.store(&HostId::new("alpha"), "abc-123").await.unwrap();
        let value = store.load(&HostId::new("alpha")).await.unwrap();
        assert_eq!(value.as_deref(), Some("abc-123"));
    }

    #[tokio::test]
    async fn file_persists_across_instances() {
        let dir = temp_dir();
        let writer = FileClientIdStore::new(dir.path());
        writer
            .store(&HostId::new("h1"), "preserved-id")
            .await
            .unwrap();
        // Simulate a restart: build a fresh store rooted at the
        // same directory and verify the prior write is observable.
        let reader = FileClientIdStore::new(dir.path());
        let value = reader.load(&HostId::new("h1")).await.unwrap();
        assert_eq!(value.as_deref(), Some("preserved-id"));
    }

    #[tokio::test]
    async fn file_keys_per_host() {
        let dir = temp_dir();
        let store = FileClientIdStore::new(dir.path());
        store.store(&HostId::new("a"), "id-a").await.unwrap();
        store.store(&HostId::new("b"), "id-b").await.unwrap();
        assert_eq!(
            store.load(&HostId::new("a")).await.unwrap().as_deref(),
            Some("id-a")
        );
        assert_eq!(
            store.load(&HostId::new("b")).await.unwrap().as_deref(),
            Some("id-b")
        );
    }

    #[tokio::test]
    async fn file_overwrites() {
        let dir = temp_dir();
        let store = FileClientIdStore::new(dir.path());
        store.store(&HostId::new("h"), "first").await.unwrap();
        store.store(&HostId::new("h"), "second").await.unwrap();
        let value = store.load(&HostId::new("h")).await.unwrap();
        assert_eq!(value.as_deref(), Some("second"));
    }

    #[tokio::test]
    async fn file_persists_host_ids_with_unsafe_characters() {
        let dir = temp_dir();
        let store = FileClientIdStore::new(dir.path());
        let tricky = HostId::new("copilot://tunnel/foo bar?baz=1");
        store.store(&tricky, "tricky-id").await.unwrap();
        let value = store.load(&tricky).await.unwrap();
        assert_eq!(value.as_deref(), Some("tricky-id"));
    }

    #[tokio::test]
    async fn file_concurrent_stores_do_not_corrupt() {
        let dir = temp_dir();
        let store = std::sync::Arc::new(FileClientIdStore::new(dir.path()));
        let mut handles = Vec::new();
        for i in 0..32 {
            let store = store.clone();
            handles.push(tokio::spawn(async move {
                store
                    .store(&HostId::new(format!("h-{i}")), &format!("id-{i}"))
                    .await
                    .unwrap();
            }));
        }
        for h in handles {
            h.await.unwrap();
        }
        for i in 0..32 {
            let value = store.load(&HostId::new(format!("h-{i}"))).await.unwrap();
            assert_eq!(value.as_deref(), Some(format!("id-{i}").as_str()));
        }
    }

    #[cfg(unix)]
    #[tokio::test]
    async fn file_is_restricted_to_owner_on_unix() {
        use std::os::unix::fs::PermissionsExt;
        let dir = temp_dir();
        let store = FileClientIdStore::new(dir.path());
        store.store(&HostId::new("h"), "value").await.unwrap();
        let path = store.path_for(&HostId::new("h"));
        let perms = std::fs::metadata(&path).unwrap().permissions();
        assert_eq!(perms.mode() & 0o777, 0o600);
    }

    #[test]
    fn encode_host_id_preserves_unreserved_set() {
        let id = HostId::new("local-host_42.example~ok");
        assert_eq!(encode_host_id(&id), "local-host_42.example~ok");
    }

    #[test]
    fn encode_host_id_escapes_path_separators_and_specials() {
        let id = HostId::new("copilot://tunnel/foo bar?baz=1");
        let encoded = encode_host_id(&id);
        assert!(!encoded.contains('/'));
        assert!(!encoded.contains(':'));
        assert!(!encoded.contains(' '));
        assert!(!encoded.contains('?'));
        assert!(encoded.contains("%2F"));
    }
}
