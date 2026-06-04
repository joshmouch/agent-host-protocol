using System.Runtime.CompilerServices;

// Allow the test project to exercise internal members (e.g. the reconnect
// backoff calculation) without widening the public API surface.
[assembly: InternalsVisibleTo("AgentHostProtocol.Tests")]
