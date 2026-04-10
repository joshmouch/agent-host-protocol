# Agent Host Protocol

A synchronized, multi-client state protocol for AI agent sessions.

**[Read the documentation →](https://microsoft.github.io/agent-host-protocol/)**

## Overview

The Agent Host Protocol (AHP) defines how a portable, standalone session server communicates with its clients. Multiple clients can connect to the server and see a synchronized view of AI agent sessions through immutable state, pure reducers, and write-ahead reconciliation.

## Development

```bash
# Install dependencies
npm install

# Start local dev server
npm run docs:dev

# Build for production
npm run docs:build

# Preview production build
npm run docs:preview
```

## License

MIT
