# Round-trip corpus — known fidelity gaps

The fixtures in this directory form a language-agnostic round-trip corpus.
Each fixture is a wire payload that every client decodes into its generated
types and re-encodes; the re-encoded value must match the original (modulo
null/empty normalization). The corpus pins forward-compatibility and exact-bit
fidelity across the reference clients.

Most fixtures round-trip cleanly on every client. The case below is a genuine,
documented gap. Each client that cannot round-trip these fixtures records them
in an explicit known-gap set and asserts that the set of fixtures it actually
skips equals that declared set — so a gap that silently closes (or a new gap
that silently opens) trips a drift tripwire in the test rather than passing
unnoticed.

## Representational gap — unknown wire keys (fixtures 017 and 019)

`017-unknown-wire-keys-ignored` and `019-channel-scoped-notification-uri` both
carry extra, unmodeled keys on the wire (`unknownFutureKey`, `anotherUnknown`)
with `expectReencodedAbsent` asserting those keys are dropped on re-encode.
Clients with a runtime decoder model unknown keys as a passthrough and re-emit
them verbatim — they drop the key on decode, so the re-encoded output omits it
and the assertion passes. The TypeScript client has compile-time types only (no
runtime decoder), so unknown keys it does not model survive intact through
`JSON.parse`→`JSON.stringify`, and the `expectReencodedAbsent` assertion fails
for both fixtures. This is a genuine type-system representational gap; it is
recorded with a drift tripwire and closes automatically if a validating/passthrough
decoder is added.
