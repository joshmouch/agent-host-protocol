---
description: Rules for iterating on protocol types
applyTo: 'types/**/*.ts'
---

- Always prefer dispatching state actions (that can cause side effects) rather than making imperative RPC calls. For example, rather than a `sendMessage` command, we have a `session/turnStarted` message.
- If a state is invalid, it should be inexpressible. For example, don't do something like

  ```
  interface IApproval {
    denied: boolean;
    reason?: string; // only present if denied=true
  }
  ```

  Instead, use a discriminated union:

  ```
  interface IDenial {
    denied: true;
    reason: string;
  }
  interface IApproval {
    denied: false;
  }
  type IApproval = IDenial | IApproval;
  ```

  Or just inline it if it's a single property:

  ```
  interface IApproval {
    deniedReason?: string; // if present, implies denied
  }
  ```

- `number` types are assumed to be 64-bit integers. If a floating point values are reasonable for a field, you MUST annotate its jsdoc with `@format float`
- For actions or commands that could be implemented by returning an array `T[]` directly, still prefer to wrap it in `{ items: T[] }` for forward compatibility. This allows adding additional fields later without breaking the shape.
- Naming discriminants for discriminated unions:
  - Lifecycle / state-machine unions: name the union `Foo*State` and its discriminant enum `Foo*Status`. Variant interfaces are `Foo*State` (e.g. `ToolCallState` + `ToolCallStatus` + `ToolCallStreamingState`; `McpServerState` + `McpServerStatus` + `McpServerStartingState`; `CustomizationLoadState` + `CustomizationLoadStatus`).
  - General/typological unions (not a lifecycle): name the discriminant `Foo*Kind` (e.g. `MessageAttachment` + `MessageAttachmentKind`, `ResponsePart` + `ResponsePartKind`, `ToolCallContributor` + `ToolCallContributorKind`).
  - Generator note: variant interface names must differ from the union wrapper names emitted by the per-language generators (e.g. Kotlin emits `value class FooStateStarting(val value: FooStartingState)`), so name variants `Foo*State` rather than `FooStatus*`.
- After making your changes, check to make sure the documentation in `docs` is up to date. For significant new flows or features, consider adding new documentation for it. Note that Mermaid diagrams are allowed.
- Whenever you change or add an action, you must review the reducers in `types/reducers.ts` to see if that needs to be propagated into the state. If it does, add the appropriate logic and unit tests for it.
- Actions that mutate a keyed collection in state (an array whose entries are identified by a stable key such as `id`, `clientId`, `resource`, or a URI — e.g. `chats`, `customizations`, `files`, `annotations`, `activeClients`) MUST follow the established add/remove/update convention rather than inventing a new shape:
  - **Upsert** (`Foo*Set`): the action carries the **full entry object**. The reducer finds the entry by key, **appends** it when absent and **replaces** it in place when present (never duplicating a key). Always name a generic create-or-replace action `Set` — not `Added`, `Changed`, or `Updated` — so the upsert convention is recognisable at a glance.
  - **Remove** (`Foo*Removed`): the action carries **only the key** (e.g. `{ clientId }`, `{ fileId }`), never the whole object. The reducer is a **no-op returning the original `state`** when no entry matches.
  - **Partial update** (`Foo*Updated`): the action carries the **key plus the optional fields that changed**; the reducer merges them onto the existing entry and is a **no-op returning `state`** when no entry matches. Ignore the key inside any `changes` payload so it can't be reassigned.
  - Prefer a key-only **remove** action over an upsert that accepts a nullable/sentinel "unset" value (e.g. do not model removal as `Changed` with `entry: null`).
  - Reducer mechanics are uniform: `const idx = list.findIndex(x => x.<key> === action.<key>)`, branch on `idx < 0`, copy immutably (`list.slice()` / `[...list]`), then write or `splice`, and return `{ ...state, <collection>: next }`. Every branch (insert, replace, remove, no-op) needs a fixture in `types/test-cases/reducers/` to keep `types/reducers.ts` at 100% branch coverage.
- Never update the protocol version unless you were instructed to do so.

## Finalizing changes

Before declaring a protocol repo change complete, run `npm run generate` and `npm run test` from the repo root. Resolve any generated-output, typecheck, lint, test, or generator issues those commands expose before handing the change back.
