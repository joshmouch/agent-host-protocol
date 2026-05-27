//! Tests for [`ahp::hosts::FileClientIdStore`].
//!
//! Cover round-trips, restart simulation (fresh instances see prior
//! writes), per-host isolation, overwrites, URL-unsafe host ids,
//! concurrent writes, and (Unix only) that persisted files end up with
//! owner-only `0o600` mode.

use std::sync::Arc;

use ahp::hosts::{ClientIdStore, FileClientIdStore, HostId};
use tempdir_helper::TempDir;

#[tokio::test]
async fn load_returns_none_for_unknown_host() {
    let dir = TempDir::new("ahp-cidstore");
    let store = FileClientIdStore::new(dir.path());
    assert!(store
        .load(HostId::new("never-stored"))
        .await
        .unwrap()
        .is_none());
}

#[tokio::test]
async fn store_and_load_round_trips() {
    let dir = TempDir::new("ahp-cidstore");
    let store = FileClientIdStore::new(dir.path());
    store
        .store(HostId::new("alpha"), "abc-123".into())
        .await
        .unwrap();
    let value = store.load(HostId::new("alpha")).await.unwrap();
    assert_eq!(value.as_deref(), Some("abc-123"));
}

#[tokio::test]
async fn survives_across_instances() {
    let dir = TempDir::new("ahp-cidstore");
    {
        let writer = FileClientIdStore::new(dir.path());
        writer
            .store(HostId::new("h1"), "preserved-id".into())
            .await
            .unwrap();
    }
    // Simulate a restart by constructing a fresh store rooted at the
    // same directory.
    let reader = FileClientIdStore::new(dir.path());
    let value = reader.load(HostId::new("h1")).await.unwrap();
    assert_eq!(value.as_deref(), Some("preserved-id"));
}

#[tokio::test]
async fn stores_are_keyed_per_host() {
    let dir = TempDir::new("ahp-cidstore");
    let store = FileClientIdStore::new(dir.path());
    store.store(HostId::new("a"), "id-a".into()).await.unwrap();
    store.store(HostId::new("b"), "id-b".into()).await.unwrap();
    assert_eq!(
        store.load(HostId::new("a")).await.unwrap().as_deref(),
        Some("id-a")
    );
    assert_eq!(
        store.load(HostId::new("b")).await.unwrap().as_deref(),
        Some("id-b")
    );
}

#[tokio::test]
async fn store_overwrites_previous_value() {
    let dir = TempDir::new("ahp-cidstore");
    let store = FileClientIdStore::new(dir.path());
    store.store(HostId::new("h"), "first".into()).await.unwrap();
    store
        .store(HostId::new("h"), "second".into())
        .await
        .unwrap();
    assert_eq!(
        store.load(HostId::new("h")).await.unwrap().as_deref(),
        Some("second")
    );
}

#[tokio::test]
async fn store_preserves_whitespace_in_client_id() {
    // The trait contract is "round-trip the exact bytes you wrote".
    // Trimming on `load` would silently corrupt ids that happen to
    // contain leading/trailing whitespace.
    let dir = TempDir::new("ahp-cidstore");
    let store = FileClientIdStore::new(dir.path());
    let raw = "  spaced-id\n";
    store.store(HostId::new("h"), raw.into()).await.unwrap();
    assert_eq!(
        store.load(HostId::new("h")).await.unwrap().as_deref(),
        Some(raw)
    );
}

#[tokio::test]
async fn store_returns_clear_error_when_directory_path_is_a_file() {
    // Pointing the store at a path that exists but isn't a directory
    // should fail fast with a recognizable error instead of letting
    // later syscalls surface a less-obvious `NotADirectory`.
    let parent = TempDir::new("ahp-cidstore");
    let bogus = parent.path().join("not-a-dir");
    std::fs::write(&bogus, b"placeholder").unwrap();

    let store = FileClientIdStore::new(&bogus);
    let err = store
        .store(HostId::new("h"), "value".into())
        .await
        .expect_err("store should fail when its directory path is a regular file");
    assert_eq!(err.kind(), std::io::ErrorKind::InvalidInput);
}

#[tokio::test]
async fn url_unsafe_host_id_round_trips() {
    let dir = TempDir::new("ahp-cidstore");
    let store = FileClientIdStore::new(dir.path());
    let tricky = HostId::new("copilot://tunnel/foo bar?baz=1");
    store
        .store(tricky.clone(), "tricky-id".into())
        .await
        .unwrap();
    assert_eq!(
        store.load(tricky).await.unwrap().as_deref(),
        Some("tricky-id")
    );
}

#[tokio::test]
async fn concurrent_stores_do_not_corrupt() {
    let dir = TempDir::new("ahp-cidstore");
    let store = Arc::new(FileClientIdStore::new(dir.path()));
    let mut joins = Vec::new();
    for i in 0..32 {
        let store = store.clone();
        joins.push(tokio::spawn(async move {
            store
                .store(HostId::new(format!("h-{i}")), format!("id-{i}"))
                .await
                .unwrap();
        }));
    }
    for j in joins {
        j.await.unwrap();
    }
    for i in 0..32 {
        let value = store.load(HostId::new(format!("h-{i}"))).await.unwrap();
        assert_eq!(value.as_deref(), Some(format!("id-{i}").as_str()));
    }
}

#[cfg(unix)]
#[tokio::test]
async fn persisted_file_is_owner_only_on_unix() {
    use std::os::unix::fs::PermissionsExt;

    let dir = TempDir::new("ahp-cidstore");
    let store = FileClientIdStore::new(dir.path());
    store.store(HostId::new("h"), "value".into()).await.unwrap();

    // Expected filename: "h" passes percent-encoding through unchanged
    // (it's a single unreserved character), so the file lands at
    // `<dir>/h.clientid`.
    let path = dir.path().join("h.clientid");
    let meta = std::fs::metadata(&path).expect("persisted file exists");
    let mode = meta.permissions().mode() & 0o777;
    assert_eq!(
        mode, 0o600,
        "expected owner-only permissions on persisted client id file"
    );
}

#[cfg(unix)]
#[tokio::test]
async fn created_directory_is_owner_only_on_unix() {
    use std::os::unix::fs::PermissionsExt;

    // Pick a subdirectory that does NOT exist yet so the store creates
    // it on first `store` and we can assert on its mode.
    let parent = TempDir::new("ahp-cidstore");
    let nested = parent.path().join("nested");
    let store = FileClientIdStore::new(&nested);
    store.store(HostId::new("h"), "value".into()).await.unwrap();

    let meta = std::fs::metadata(&nested).expect("directory exists");
    let mode = meta.permissions().mode() & 0o777;
    assert_eq!(
        mode, 0o700,
        "expected owner-only permissions on newly-created store directory"
    );
}

/// Tiny zero-dep tempdir helper so the test file doesn't add a new
/// workspace dependency. Removes the directory on drop.
mod tempdir_helper {
    use std::path::{Path, PathBuf};
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    pub struct TempDir(PathBuf);

    impl TempDir {
        pub fn new(prefix: &str) -> Self {
            static COUNTER: AtomicU64 = AtomicU64::new(0);
            let n = COUNTER.fetch_add(1, Ordering::Relaxed);
            let now_nanos = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos();
            let pid = std::process::id();
            let path = std::env::temp_dir().join(format!("{prefix}-{pid}-{now_nanos}-{n}"));
            std::fs::create_dir_all(&path).expect("create temp dir");
            Self(path)
        }

        pub fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
}
