package main

import (
	"context"
	"encoding/base64"
	"fmt"
	"time"

	"github.com/aws/aws-lambda-go/lambda"
)

type Remote struct {
	IpAddress string `json:"IpAddress"`
}

type Local struct {
	Domain string `json:"Domain"`
	Port   int    `json:"Port"`
}

type Message struct {
	Tag        string `json:"Tag"`
	Remote     Remote `json:"Remote"`
	Local      Local  `json:"Local"`
	ReceivedAt string `json:"ReceivedAt"`
	Formatter  string `json:"Formatter"`
	Data       string `json:"Data"`
}

type InboundPackets struct {
	Messages []Message `json:"Messages"`
}

type OutboundPacket struct {
	GeneratedAt string `json:"GeneratedAt"`
	Tag         string `json:"Tag"`
	Data        string `json:"Data"`
}

type Response struct {
	Replies []*OutboundPacket `json:"Replies"`
}

func handler(ctx context.Context, inbound InboundPackets) (Response, error) {
	// Count packets per source IP
	counts := make(map[string]int)
	for _, msg := range inbound.Messages {
		counts[msg.Remote.IpAddress]++
	}

	// Helper to get & clear count and encode
	getAndClear := func(m map[string]int, key string) *string {
		value, ok := m[key]
		if !ok || value == 0 {
			return nil
		}
		m[key] = 0
		encoded := base64.StdEncoding.EncodeToString([]byte(fmt.Sprintf("%d\n", value)))
		return &encoded
	}

	// Build replies
	var replies []*OutboundPacket
	for _, msg := range inbound.Messages {
		data := getAndClear(counts, msg.Remote.IpAddress)
		if data == nil {
			continue
		}
		replies = append(replies, &OutboundPacket{
			GeneratedAt: time.Now().UTC().Format(time.RFC3339),
			Tag:         msg.Tag,
			Data:        *data,
		})
	}

	return Response{Replies: replies}, nil
}

func main() {
	lambda.Start(handler)
}
