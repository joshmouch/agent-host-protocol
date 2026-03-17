<!-- Generated from types/*.ts — do not edit -->


# Notifications

Notifications are ephemeral broadcasts that are **not** part of the state tree. They are not processed by reducers and are not replayed on reconnection.

## Protocol Notifications

### `notify/sessionAdded` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/notifications.ts#L49" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Broadcast to all connected clients when a new session is created.

| Field | Type | Description |
|---|---|---|
| `type` | `NotificationType.SessionAdded` |  |
| `summary` | [ISessionSummary](/reference/state-types#isessionsummary) | Summary of the new session |

**Example:**

```json
{
  "jsonrpc": "2.0",
  "method": "notification",
  "params": {
    "notification": {
      "type": "notify/sessionAdded",
      "summary": {
        "resource": "copilot:/<uuid>",
        "provider": "copilot",
        "title": "New Session",
        "status": "idle",
        "createdAt": 1710000000000,
        "modifiedAt": 1710000000000
      }
    }
  }
}
```

### `notify/sessionRemoved` <a href="https://github.com/microsoft/agent-host-protocol/blob/main/types/notifications.ts#L74" title="View source" style="float:right;font-size:0.75em;opacity:0.5;text-decoration:none">📄</a>

Broadcast to all connected clients when a session is disposed.

| Field | Type | Description |
|---|---|---|
| `type` | `NotificationType.SessionRemoved` |  |
| `session` | [URI](/reference/state-types#uri) | URI of the removed session |

**Example:**

```json
{
  "jsonrpc": "2.0",
  "method": "notification",
  "params": {
    "notification": {
      "type": "notify/sessionRemoved",
      "session": "copilot:/<uuid>"
    }
  }
}
```

## Usage Pattern

Clients use notifications to maintain a local session list cache:

1. On connect, fetch the full session list via `listSessions()`.
2. Listen for `notify/sessionAdded` and `notify/sessionRemoved` to keep the cache updated.
3. On reconnect, **re-fetch** the full list — notifications are not replayed.

## Version Introduction

| Notification Type | Version |
|---|---|
| `notify/sessionAdded` | 1 |
| `notify/sessionRemoved` | 1 |

## Server Notifications

In addition to protocol notifications, the server pushes action envelopes to subscribed clients:

### `action`

Wraps an `ActionEnvelope` for delivery to subscribed clients:

```json
{
  "jsonrpc": "2.0",
  "method": "action",
  "params": {
    "envelope": {
      "action": { "type": "session/delta", ... },
      "serverSeq": 43,
      "origin": { "clientId": "client-1", "clientSeq": 1 }
    }
  }
}
```
