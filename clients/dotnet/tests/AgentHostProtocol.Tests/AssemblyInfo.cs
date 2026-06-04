// Run tests sequentially. The suite mixes fast unit tests with a real-socket
// integration test (LiveSocketIntegrationTests); serializing avoids the
// integration test being starved of the thread pool by parallel unit tests.
// The whole suite still runs in a couple of seconds.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
