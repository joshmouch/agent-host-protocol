// AHP Go conformance runner — build-phase B5.
// Standalone module so it can import the in-repo Go client via a replace
// directive without polluting the client's own go.mod.
module github.com/microsoft/agent-host-protocol/conformance/go

go 1.23

replace github.com/microsoft/agent-host-protocol/clients/go => ../../clients/go

require (
	github.com/coder/websocket v1.8.14
	github.com/microsoft/agent-host-protocol/clients/go v0.0.0-00010101000000-000000000000
)
