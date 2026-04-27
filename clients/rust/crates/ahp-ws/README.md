# ahp-ws

WebSocket transport for the [Agent Host Protocol (AHP)](https://github.com/microsoft/agent-host-protocol) Rust SDK.

[![crates.io](https://img.shields.io/crates/v/ahp-ws.svg)](https://crates.io/crates/ahp-ws)
[![docs.rs](https://img.shields.io/docsrs/ahp-ws)](https://docs.rs/ahp-ws)

Implements [`ahp::Transport`](https://docs.rs/ahp/latest/ahp/transport/trait.Transport.html) using [`tokio-tungstenite`](https://crates.io/crates/tokio-tungstenite), supporting both `ws://` and `wss://` (TLS via `native-tls`).

## Usage

```toml
[dependencies]
ahp = "0.1"
ahp-ws = "0.1"
tokio = { version = "1", features = ["full"] }
```

```rust
use ahp::{Client, ClientConfig, SubscriptionEvent};
use ahp_ws::WebSocketTransport;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let transport = WebSocketTransport::connect("ws://localhost:12345").await?;
    let client = Client::connect(transport, ClientConfig::default()).await?;

    client.initialize("my-client".into(), 1, vec!["root:/".into()]).await?;

    let mut sub = client.attach_subscription("root:/").await;
    while let Some(SubscriptionEvent::Action(a)) = sub.recv().await {
        println!("{:?}", a.action);
    }

    client.shutdown().await;
    Ok(())
}
```

## API

- **[`WebSocketTransport::connect(url)`](https://docs.rs/ahp-ws/latest/ahp_ws/struct.WebSocketTransport.html#method.connect)** — open a new connection
- **[`WebSocketTransport::from_stream(stream)`](https://docs.rs/ahp-ws/latest/ahp_ws/struct.WebSocketTransport.html#method.from_stream)** — wrap an existing `tokio-tungstenite` stream for custom TLS or connection options

## See also

- [`ahp`](https://crates.io/crates/ahp) — the main client crate
- [`ahp-types`](https://crates.io/crates/ahp-types) — wire types only
- [Protocol documentation](https://microsoft.github.io/agent-host-protocol/)
