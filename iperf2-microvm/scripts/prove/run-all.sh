#!/usr/bin/env bash
#
# Runs every proof against the deployed stack and collects the evidence in evidence/<timestamp>/:
# summary.md (every PASS/FAIL line), the raw client output, and the backend log events used.
#
# Usage: scripts/prove/run-all.sh
#   IMPAIR=1     also run 03-impairment.sh (needs sudo for tc netem)
#   FRESH=1      start from a brand new MicroVM first (scripts/fresh-vm.sh)
#   ORACLE=1     (default) compare scenario A with a local stock iperf2 server
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
export EVIDENCE_DIR="$HERE/../../evidence/$STAMP"
export ORACLE=${ORACLE:-1}
mkdir -p "$EVIDENCE_DIR"
{
  echo "# iperf2 on Proxylity UDP Gateway + Lambda MicroVM: proof run $STAMP"
  echo
  echo "- client: $(iperf -v 2>&1 | head -1)"
  echo "- host: $(uname -sr)"
} > "$EVIDENCE_DIR/summary.md"

[[ "${FRESH:-0}" == 1 ]] && bash "$HERE/../fresh-vm.sh"

STEPS=(00-preflight.sh 01-deterministic.sh 02-stock-client.sh)
[[ "${IMPAIR:-0}" == 1 ]] && STEPS+=(03-impairment.sh)
STEPS+=(04-parallel.sh 05-state.sh)

declare -A RESULT
for step in "${STEPS[@]}"; do
  echo; echo "################ $step"
  if bash "$HERE/$step"; then RESULT[$step]=PASS; else RESULT[$step]=FAIL; fi
done

{
  echo; echo "## Results"; echo
  echo "| Proof | Result |"; echo "|---|---|"
  for step in "${STEPS[@]}"; do echo "| $step | ${RESULT[$step]} |"; done
} >> "$EVIDENCE_DIR/summary.md"

echo; echo "Evidence: $EVIDENCE_DIR"
FAILED=0
for step in "${STEPS[@]}"; do printf '  %-22s %s\n' "$step" "${RESULT[$step]}"; [[ ${RESULT[$step]} == PASS ]] || FAILED=1; done
exit $FAILED
