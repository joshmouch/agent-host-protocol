# Round-trip corpus — known fidelity gaps

The fixtures in this directory form a language-agnostic round-trip corpus.
Each fixture is a wire payload that every client decodes into its generated
types and re-encodes; the re-encoded value must match the original (modulo
null/empty normalization). The corpus pins forward-compatibility and exact-bit
fidelity across the reference clients.

Most fixtures round-trip cleanly on every client. The two cases below are
genuine, documented gaps. Each client that cannot round-trip one of these
fixtures records it in an explicit known-gap set and asserts that the set of
fixtures it actually skips equals that declared set — so a gap that silently
closes (or a new gap that silently opens) trips a drift tripwire in the test
rather than passing unnoticed.

## Representational gap — unknown wire keys (fixture 017)

`017-unknown-wire-keys-ignored` carries extra, unmodeled keys on the wire.
Clients with a runtime decoder model unknown keys as a passthrough and re-emit
them verbatim. The TypeScript client has compile-time types only (no runtime
decoder), so unknown keys it does not model cannot survive a decode→re-encode
and are dropped. This is the one genuine type-system representational gap in the
corpus; it is recorded with a drift tripwire and closes automatically if a
validating/passthrough decoder is added.

## Schema-invalid fixture skip (fixture 019)

`019-channel-scoped-notification-uri` exercises a channel-scoped notification
URI, but its payload is missing a schema-required field. Clients that validate
against the schema skip this fixture explicitly rather than letting the suite's
status depend on malformed input. The skip is recorded in each client's
known-gap set and closes once the fixture payload is repaired to a schema-valid
shape.
