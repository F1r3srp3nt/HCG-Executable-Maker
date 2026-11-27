// hcg-agent: the host agent helper of the AgentHelperApp fixture (shaped like HCG Server's agent).
package main

import (
	"fmt"
	"os"
	"runtime"
)

const version = "1.0.0"

func main() {
	if len(os.Args) > 1 && os.Args[1] == "version" {
		fmt.Printf("hcg-agent %s (%s/%s)\n", version, runtime.GOOS, runtime.GOARCH)
		return
	}
	fmt.Fprintln(os.Stderr, "usage: hcg-agent version")
	os.Exit(2)
}
