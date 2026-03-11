-- UDP message ingestion table
-- Generic schema for storing Proxylity batch packet data.
-- Extend column definitions to match your device's wire protocol.

CREATE TABLE IF NOT EXISTS udp_messages (
  id          BIGSERIAL    PRIMARY KEY,
  tag         TEXT         NOT NULL,          -- Proxylity Tag — matches RequestPacket.Tag for reply correlation
  received_at TIMESTAMPTZ  NOT NULL,          -- Packet arrival time from RequestPacket.ReceivedAt
  source_ip   TEXT         NOT NULL,          -- Sender IP from RequestPacket.Remote.IpAddress
  source_port INTEGER      NOT NULL,          -- Sender port from RequestPacket.Remote.Port
  payload     TEXT         NOT NULL,          -- Decoded UTF-8 string from RequestPacket.Data
  created_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
);

-- Time-series queries (default read pattern for IoT data)
CREATE INDEX idx_udp_messages_received_at ON udp_messages (received_at DESC);

-- Per-device queries
CREATE INDEX idx_udp_messages_source_ip ON udp_messages (source_ip, received_at DESC);

-- Enable Row Level Security.
-- The service role key (used by the Edge Function) bypasses RLS automatically.
-- Add SELECT policies below to expose rows to authenticated or anonymous users,
-- for example to power a Supabase Realtime subscription in a browser dashboard.
ALTER TABLE udp_messages ENABLE ROW LEVEL SECURITY;
