# Archive-state convergence conformance

This suite treats `session/isArchivedChanged` as a final-state action. A
non-rejected `ActionEnvelope` is the one authoritative AHP settlement: it means
the archive authority has completed the requested transition, not merely queued
an intent. A host that wants asynchronous intent instead needs a separately
specified requested/lifecycle action.

The suite is data-driven. The runner owns every assertion; a host/provider row
owns only fixture creation and observation at its real integration boundaries.
Production archive clients remain ordinary AHP clients and do not query native
providers as a second authority.

## Row identity and matrix

`implementationId` identifies host executable source.
`clientImplementationId` independently identifies the client/reconciler used to
dispatch and project the transition. `deploymentId` distinguishes profiles,
accounts, machines, and managed instances of the same host executable. Several
deployments of one executable are coverage instances of one row, not new
implementations. Host/provider authority results and client reconciliation
results therefore remain orthogonal and cannot be inferred from each other.

| Host implementation candidate | Provider candidate | Authority | Row owner | Current gate |
| --- | --- | --- | --- | --- |
| `microsoft/vscode:code-agent-host` | Codex | `provider` | VS Code | Required first row. The provider query is Codex app-server `thread/list` for the exact thread. |
| `microsoft/vscode:code-agent-host` | Claude | `host` | VS Code | Required row and currently red/GAP until the soft AHP state survives the declared host restart/reopen boundary. Claude has no `IAgent.onArchivedChanged` hook. |
| `microsoft/vscode:code-agent-host` | Copilot | `host` | VS Code | Required row and currently red/GAP until the soft AHP state survives the declared host restart/reopen boundary. Copilot has no `IAgent.onArchivedChanged` hook. |
| `joshmouch/ahp-host` | Grok ACP/CLI | `host` | ahp-host/Grok adopter | Passing host-authority row: archive state is persisted atomically, and `reopenAndQueryArchived` constructs a fresh host/authority and performs a fresh backend catalog query. Native Grok provider archive remains explicitly unsupported (`archive: false`); ACP close/delete are not reclassified as archive. |
| `OpenAgency/main/pods/ahp-host` | Prototype adapters | Non-runnable prototype | OpenAgency | Inventory-only target. It is not counted as a production or passing row. |
| Conflux AHP proof/conformance targets | Proof/prototype adapters | Non-runnable prototype | Conflux | Inventory-only target. Proof mirrors and generated conformance artifacts are not runnable production host rows. |

Grok deployments that invoke `joshmouch/ahp-host` are instances of that one
implementation row. They are not separate “Grok host” implementations.

The current client coverage matrix is separate from the authority matrix:

| Client implementation | Required projections | Current gate |
| --- | --- | --- |
| `microsoft/agent-host-protocol:typescript-client` | Exact settlement correlation, optimistic rollback, confirmed-state rebasing, and lossless reconnect feed | `dispatchAndWait` supplies exact settlement correlation. The generic optimistic reconciler and lossless reconnect feed remain a contribution target. |
| `microsoft/vscode:agent-host-protocol-client` | Exact settlement correlation, `AgentSubscription` rollback/rebase/replay, and VS Code UI projection | Required VS Code integration row. VS Code-specific observables, URI mapping, reference counting, transport, and editor integration remain in VS Code. |

AI Fleet dogfoods the TypeScript SDK client against both production host
implementations. Codex/VS Code Desktop dogfoods the VS Code client against the
VS Code host and, where transport/discovery supports it, `joshmouch/ahp-host`.
The shared runner compares exact settlement, rollback, and reconnect behavior;
it does not turn authority verification into a client responsibility.

## Adapter API

The distributable TypeScript contract and runner are exported as:

```ts
import {
  defineArchiveStateConformanceRow,
  runArchiveStateConformance,
} from '@microsoft/agent-host-protocol/conformance/archive-state';
```

Their handwritten source lives under
`clients/typescript/src/conformance/archive-state/`; it imports generated AHP
types from the same package and never requires a sibling checkout. Every
adapter supplies:

1. One stable host `implementationId`, one stable
   `clientImplementationId`, an optional `deploymentId`, and a provider
   identity.
2. An explicit negotiated-version applicability declaration. For each member
   of canonical `SUPPORTED_PROTOCOL_VERSIONS`, the runner offers that singleton,
   performs the real initialize negotiation, and classifies the exact returned
   version before fixture work.
3. The required feature as the action identity
   `session/isArchivedChanged`. The runner derives its introduction version from
   `ACTION_INTRODUCED_IN`; rows do not copy a version literal.
4. Fixture creation, optimistic dispatch, an envelope observer, AHP server
   status, initiating-client projection, other-client projection, and an
   optional UI projection.
5. An authority-specific probe:
   - `provider` rows expose provider-request start/completion and queries for
     the exact resource. The runner records `immediate` when the first query
     converges, otherwise waits once and records `delayed`; continued staleness
     fails as `never-converged`.
   - `host` rows expose a durable query that crosses their declared
     restart/reopen boundary.
6. Archive and unarchive cleanup.

The generic runner asserts these as separate observations:

- the initiating client applies the optimistic value;
- no accepted final-state envelope precedes authority settlement;
- the server's `SessionStatus.IsArchived` does not lead settlement;
- non-originating clients never apply a rejected action;
- rejection carries `rejectionReason` and restores the initiating client to the
  last confirmed value;
- success emits exactly one accepted transition;
- provider convergence is classified without collapsing `delayed` into
  `immediate`, while host authority is exact across restart/reopen;
- the applicable UI projection converges;
- unarchive returns every authority and projection to the original state.

## Fail-closed version applicability

The canonical release roster in `types/version/registry.ts` derives the closed
`NegotiableProtocolVersion` type. Each row is total over that type and assigns
every version exactly one disposition: `run`, `same-as`, `not-introduced`,
capability-conditioned, or `superseded` with a replacement and migration note.

After real negotiation, the runner behaves as follows:

- an exact behavior run offers one version and requires that exact negotiated
  result; multi-offer preference and downgrade belong to negotiation tests;
- `run` and `same-as` execute only if the canonical action lifecycle says
  `session/isArchivedChanged` exists at that version;
- capability-conditioned behavior is a separate conjunctive axis;
- an explicitly superseded version resolves to an applicable replacement;
  missing targets and cycles are hard failures;
- every other version—including a newer version from a future registry—is a
  hard `unclassified-protocol-version` error naming the negotiated version and
  requiring a deliberate row-contract update.

There is no skip, xfail, broad “latest,” or silent fallback classification.
