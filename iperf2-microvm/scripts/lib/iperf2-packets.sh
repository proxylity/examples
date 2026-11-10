#!/usr/bin/env bash
#
# Build and decode iperf2 UDP datagrams in plain bash (printf + xxd), and send them with bash's
# built-in /dev/udp. This is how the deterministic proof sends traffic whose correct answer is
# known in advance, without depending on the iperf client.
#
# Datagram layout (big-endian), as captured from a stock iperf 2.1.9 client:
#   0  u32  sequence (low 32 bits; negative = FIN)
#   4  u32  sender time, seconds
#   8  u32  sender time, microseconds
#  12  u32  sequence (high 32 bits; ffffffff on a FIN)
#  16  u32  client flags (first datagram; 0x08000000 = 64-bit sequence numbers)
#  ...      padding to the datagram length
#
# Server report layout (64-bit form, 128 bytes): 16 zero bytes, then
#  16 flags | 20 bytes hi | 24 bytes lo | 28 duration s | 32 duration us | 36 lost lo
#  40 out-of-order lo | 44 datagrams lo | 48 jitter s | 52 jitter us

# Prints n as 8 hex digits (32-bit two's complement).
hex32() { printf '%08x' $(( $1 & 0xffffffff )); }

# iperf2_datagram SEQ LENGTH  -> hex of one datagram on stdout
iperf2_datagram() {
  local seq=$1 length=${2:-100}
  local now_us; now_us=$(date +%s%6N)
  local sec=$(( now_us / 1000000 )) usec=$(( now_us % 1000000 ))
  local high=0 flags=0
  (( seq < 0 )) && high=0xffffffff
  (( seq == 1 )) && flags=0x48010098   # same flags word the stock client sends first
  local hex
  hex="$(hex32 "$seq")$(hex32 "$sec")$(hex32 "$usec")$(hex32 "$high")$(hex32 "$flags")"
  local pad=$(( length * 2 - ${#hex} ))
  printf '%s%0*d' "$hex" "$pad" 0
}

# iperf2_open HOST PORT -> opens fd 3 as a connected UDP socket (send and receive on one port)
iperf2_open() { exec 3<>"/dev/udp/$1/$2"; }
iperf2_close() { exec 3<&- 3>&-; }

# iperf2_send SEQ [LENGTH]
iperf2_send() { iperf2_datagram "$1" "${2:-100}" | xxd -r -p >&3; }

# iperf2_finish FINAL_SEQ [LENGTH] [MAX_TRIES]
# Sends FIN -FINAL_SEQ, then retries -(FINAL_SEQ+1), ... every 20 ms (like the real client) until a
# server report comes back. Prints the report as hex, or fails after MAX_TRIES.
iperf2_finish() {
  local seq=$1 length=${2:-100} tries=${3:-150} i reply
  for (( i = 0; i < tries; i++ )); do
    iperf2_send "-$(( seq + i ))" "$length"
    reply=$(timeout 0.02 dd bs=65535 count=1 status=none <&3 2>/dev/null | xxd -p | tr -d '\n')
    if [[ -n "$reply" ]]; then
      echo "$reply"
      return 0
    fi
  done
  # Final, longer wait in case the report is still in flight.
  reply=$(timeout 3 dd bs=65535 count=1 status=none <&3 2>/dev/null | xxd -p | tr -d '\n')
  [[ -n "$reply" ]] && { echo "$reply"; return 0; }
  return 1
}

# report_field HEX OFFSET -> unsigned 32-bit value at a byte offset of a report
report_field() { echo $(( 16#${1:$(( $2 * 2 )):8} )); }

# report_json HEX -> the report decoded as JSON (64-bit form)
report_json() {
  local h=$1
  local bytes=$(( ($(report_field "$h" 20) << 32) | $(report_field "$h" 24) ))
  local dur_s dur_us jit_s jit_us
  dur_s=$(report_field "$h" 28); dur_us=$(report_field "$h" 32)
  jit_s=$(report_field "$h" 48); jit_us=$(report_field "$h" 52)
  printf '{"length":%d,"bytes":%d,"durationUs":%d,"lost":%d,"outOfOrder":%d,"datagrams":%d,"jitterUs":%d}\n' \
    $(( ${#h} / 2 )) "$bytes" $(( dur_s * 1000000 + dur_us )) \
    "$(report_field "$h" 36)" "$(report_field "$h" 40)" "$(report_field "$h" 44)" $(( jit_s * 1000000 + jit_us ))
}
