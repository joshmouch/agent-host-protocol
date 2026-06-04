// Conditional lock primitive: on .NET 9+ the dedicated System.Threading.Lock
// is ~25% faster under contention than Monitor; on net8.0 we fall back to a
// plain object (classic Monitor). The `lock (gate) { ... }` statements are
// identical either way — only the field's declared type changes. See
// docs/adr/ADR-SYNC.md.
#if NET9_0_OR_GREATER
global using Gate = System.Threading.Lock;
#else
global using Gate = System.Object;
#endif
