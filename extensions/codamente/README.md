# Codamente — Agent Host Protocol Extension

A VS Code extension that registers your editor as an AHP (Agent Host Protocol) host and **automatically starts the agent host in GitHub Codespaces** — no per-repo configuration needed.

## How It Works

1. **Codespace detection** — On activation (`onStartupFinished`), the extension checks the `CODESPACE_NAME` environment variable.
2. **Agent host auto-start** — If running inside a Codespace, the extension spawns the agent host process on the configured port (default: 8081).
3. **Registry registration** — The extension registers the host with the [Codamente registry](https://codamente.com/api) so mobile/remote clients can discover and connect to it.
4. **Heartbeats** — A periodic heartbeat keeps the registration alive in the registry.
5. **Cleanup** — On deactivation (Codespace stop, window close), the extension deregisters the host and stops the agent host process.

## Settings Sync

Because VS Code extensions sync via **Settings Sync**, installing Codamente once means it's available in every Codespace you open — **zero per-repo setup required**.

## Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `codamente.registryUrl` | `https://codamente.com/api` | Base URL for the host registry API |
| `codamente.agentHostPort` | `8081` | Port the agent host listens on |
| `codamente.autoStartInCodespaces` | `true` | Auto-start when in a Codespace |
| `codamente.heartbeatIntervalSeconds` | `30` | Heartbeat interval in seconds |

## Commands

- **Codamente: Start Agent Host** — Manually start the agent host
- **Codamente: Stop Agent Host** — Stop the agent host and deregister
- **Codamente: Show Status** — Show current status (running, registered, Codespace name)

## Advanced

### Custom Agent Host Binary

Set the `CODAMENTE_AGENT_HOST_CMD` environment variable to override the default `npx @anthropic-ai/agent-host` command:

```bash
export CODAMENTE_AGENT_HOST_CMD="my-custom-host --port 8081"
```

## Development

```bash
cd extensions/codamente
npm install
npm run compile    # or: npm run watch
```

Press **F5** in VS Code to launch an Extension Development Host for testing.

## License

MIT — see [LICENSE](../../LICENSE) in the repository root.
