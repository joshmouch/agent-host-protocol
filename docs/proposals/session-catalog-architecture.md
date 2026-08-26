# Session catalog capabilities and multi-host orchestration

> Architecture proposal. This document describes a decomposition and ownership
> model for features that are not all part of the released protocol or every
> client runtime yet. The specification and generated reference pages remain the
> authority for behavior that has actually landed.

## Problem

A session-retention client needs more than a recent-session picker. It must be
able to discover the complete authorized catalog, preserve provider-native
state such as pinning, address the same-looking session on two hosts without
colliding, and finish pagination without reimplementing a client runtime.

Those needs span four different owners:

1. wire behavior that hosts and clients must agree on;
2. generated language projections of that wire behavior;
3. hand-maintained client orchestration across connections and hosts; and
4. product policy applied after the protocol data has been collected.

Treating all four as one "session pin status" feature produces a change that is
hard to review and puts behavior in generated or product-specific code. The
features below are intentionally independent unless their semantics are
atomic.

## Source ownership

| Concern | Owner |
| --- | --- |
| Status bits, actions, request and result fields, cursors, and capability shapes | Canonical sources under `types/**` |
| Language wire types and schemas | Generators and their committed outputs |
| Connection cancellation, readiness waiting, pagination exhaustion, host qualification, and cached root access | Hand-maintained client runtimes |
| Applying a catalog option or action | Each host implementation |
| Preservation rules, archive eligibility, final cross-host ordering, and batch size | The consuming product |

Generated outputs are consequences, not design inputs. In particular,
`clients/typescript/src/types/**` and the corresponding generated directories in
other clients are never edited as the source of a protocol change. A host that
vendors AHP types, including VS Code, receives those shapes through its normal
sync or generation path.

## Protocol features

### First-class session pinning

Pinning is one atomic feature:

- an `IsPinned` session-status bit;
- a client-dispatchable `session/isPinnedChanged` action;
- reducer behavior and fixtures for pin and unpin; and
- a capability stating that the host both projects and mutates pin state.

Shipping the status without the action makes the feature read-only by accident.
Shipping the action without the status leaves no authoritative value to
confirm. Pinning is not a catalog-query operator and should not be nested under
catalog selection capabilities.

### Complete authorized catalog

`listSessions` needs an optional scope that distinguishes the host's normal
catalog population from every session the current connection is authorized to
list. The intended vocabulary is:

```ts
catalogScope?: 'default' | 'complete'
```

Omission and `default` preserve ordinary host behavior. `complete` is
request-scoped: it must not mutate a host preference, depend on a notification
barrier, or imply access beyond the connection's authorization.

### Provider filter

Provider selection is an independent, optional predicate on `listSessions`.
It can be useful with either catalog scope, so it must not be coupled to
`complete` or to pinning. A host that advertises it applies the filter before
ordinary cursor pagination.

### Root-state capability advertisement

Host support is declared in root state and grouped by the behavior clients can
exercise:

```ts
interface HostCapabilities {
  sessions?: {
    pinning?: Record<string, never>;
    catalog?: {
      complete?: Record<string, never>;
      providerFilter?: Record<string, never>;
    };
  };
}
```

The exact type names are selected in the canonical sources. The architectural
requirements are that absence means unsupported, advertisements describe real
host behavior, and clients do not inspect private `_meta` keys or infer support
from a development version string.

The capability container depends on the individual features it names. It is a
small integration change after those features, not the owner of their wire
semantics.

### Deferred catalog operators

Deterministic server ordering and a query-wide result cap are useful generic
features, but they require their own design work:

- ordering needs a small, portable field vocabulary, stable tie-breaking, and
  cursor semantics; and
- a query-wide cap needs a name and contract distinct from the existing
  per-page `limit`.

Neither is required for pinning, complete enumeration, provider filtering, or a
consumer's final post-policy batch limit. They should be separate proposals
rather than hidden inside the initial catalog changes.

## Client-runtime features

The following are SDK orchestration, not additions to the wire protocol:

- offer a caller-selected protocol-version list per host;
- cancel or time-bound WebSocket connection establishment;
- wait until a selected host set has reached connected or terminal-failure
  states;
- exhaust `listSessions` cursors safely and return host-qualified summaries;
- detect repeated cursors;
- keep background catalog caches cursor-complete;
- expose the latest root snapshot through the multi-host handle;
- return an accepted-versus-rejected receipt for dispatched actions; and
- provide a typed helper for the existing authentication exchange.

These APIs belong in each language's hand-maintained runtime. They should share
behavioral vectors and an explicit parity verdict across every existing
`MultiHostClient`; they do not require identical surface syntax. A language
that has generated wire types but no multi-host runtime participates only in
protocol generation until that runtime exists.

The cursor-complete listing returns identity as `(hostId, resource)`. It does
not merge sessions that happen to have the same provider resource, and it does
not apply product-specific retention, sorting, or batch policy.

## Host and consumer boundary

A host owns truthful projection and execution: complete enumeration, provider
filtering, provider-native pin status, and pin mutation. It advertises only the
capabilities it actually implements.

A consumer owns the policy that gives those facts meaning. A retention product,
for example, still owns title/project/age selection, preservation of
pinned/running/unread/attention-required or unknown evidence, execution-target
resolution, archive confirmation, and the final global order and batch size
after every safety rule. A per-host query cap cannot replace that final global
limit.

No network-facing federation server is required for in-process aggregation.
The multi-host client already owns connection identity and fan-in; federation
becomes a separate product only when a real network boundary requires it.

## Dependency order

```text
pin status + action -----------\
complete catalog scope --------+--> root session capabilities --> host adoption
provider catalog filter -------/

generated protocol params/results --> cursor-complete multi-host listing
transport API ----------------------> bounded/cancellable connection opening
host state machine -----------------> selected-host readiness
```

The independent protocol features may be reviewed separately. Host changes
follow the protocol feature they implement. Client-runtime leaves may be
reviewed independently when they use only existing wire shapes; the hosted
catalog helper follows the generated request and result types it consumes.

## Acceptance

A protocol change is complete only when canonical types, reducers and fixtures
are green, every configured language/schema projection is regenerated, and the
generated-artifact drift checks pass. A runtime change additionally needs
language-specific tests and a parity receipt. A host change needs behavior tests
that prove the advertisement and implementation agree. Consumer cutover is the
evidence that the generic API actually removes the former wrapper without
moving product policy into the SDK or host.
