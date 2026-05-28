// Command connect_ws connects to an AHP server over WebSocket, runs
// the `initialize` handshake, subscribes to the root channel, and
// prints every inbound event as JSON until the connection drops.
//
// Usage: connect_ws ws://host:port
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"

	"github.com/microsoft/agent-host-protocol/clients/go/ahp"
	"github.com/microsoft/agent-host-protocol/clients/go/ahptypes"
	"github.com/microsoft/agent-host-protocol/clients/go/ahpws"
)

func main() {
	if len(os.Args) != 2 {
		fmt.Fprintln(os.Stderr, "usage: connect_ws ws://host:port")
		os.Exit(2)
	}
	url := os.Args[1]

	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer cancel()

	transport, err := ahpws.Connect(ctx, url)
	if err != nil {
		log.Fatalf("connect: %v", err)
	}
	client, err := ahp.Connect(ctx, transport, ahp.DefaultConfig())
	if err != nil {
		log.Fatalf("client: %v", err)
	}
	defer client.Shutdown(context.Background())

	init, err := client.Initialize(ctx, "ahp-go-example", ahptypes.SupportedProtocolVersions(), []string{ahptypes.RootResourceURI})
	if err != nil {
		log.Fatalf("initialize: %v", err)
	}
	log.Printf("negotiated protocol version: %s", init.ProtocolVersion)

	sub := client.AttachSubscription(ahptypes.RootResourceURI)
	defer sub.Close()

	for ev := range sub.Events() {
		out, _ := json.MarshalIndent(ev, "", "  ")
		fmt.Printf("%T:\n%s\n\n", ev, out)
	}
}
