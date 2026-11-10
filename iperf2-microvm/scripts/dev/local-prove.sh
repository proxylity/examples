#!/usr/bin/env bash
# Runs the deterministic proof (with the stock-server oracle) against the local stand-in, no AWS needed.
cd "$(dirname "$0")/../.."
export EVIDENCE_DIR=/tmp/iperf2-local/evidence
rm -rf "$EVIDENCE_DIR"
APP_BIN="${APP_BIN:-}" WORK_DIR="${WORK_DIR:-$HOME/iperf2-microvm}" \
  LOCAL_CMD="ORACLE=1 EVIDENCE_DIR=$EVIDENCE_DIR scripts/prove/01-deterministic.sh 127.0.0.1 5001" \
  bash scripts/dev/local-test.sh
