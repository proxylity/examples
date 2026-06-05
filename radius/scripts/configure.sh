#!/bin/bash
set -ex

#
# C O N F I G U R A T I O N
#

# Security first! To restrict use of the RADIUS services to a specific set of IP
# addresses, set the `ALLOWED_IPS` environment variable to the CIDR notation of the
# allowed IP addresses. The default is to restrict access to the current public IP (
# probably your internet gateway, so it will only be accessible from your network).
# To allow open/unrestricted access, set this to 0.0.0.0/0,::/0.
export ALLOWED_IPS="${ALLOWED_IPS:-$(curl -s checkip.amazonaws.com)/32}"

# The regions that will host handlers for the radius example. The AWS_REGION
# environment variable is used to deploy the global stack, and the DEPLOY_TO_REGIONS
# environment variable is used to deploy the regional stacks. The value of AWS_REGION
# must be one of the regions in DEPLOY_TO_REGIONS. 
export DEPLOY_TO_REGIONS="us-west-2 us-east-1 eu-west-1"

# The name of the stack to deploy.
export STACK_NAME="${STACK_NAME:-radius}"

# The transport protocol to use for the RADIUS listeners. Valid values are `UDP` and 
# `WireGuard`. The default is `WireGuard`, which is the recommended and more secure 
# option. If you choose `UDP`, the templates will deploy standard UDP-based RADIUS 
# listeners instead of WireGuard-based listeners, and the `PEER_PUBLIC_KEY` environment 
# variable will be ignored.
export TRANSPORT="${TRANSPORT:-WireGuard}"

# The public key for the WireGuard peer that will connect to the RADIUS authentication 
# and accounting listeners. Unless specified, we'll generate and save a random key pair 
# for the first peer and use the public key for both the authentication and accounting 
# listeners. To specify your own key, set the `PEER_PUBLIC_KEY` environment variable to
# a base64-encoded public key. Each key must be 32 bytes (44 characters when encoded).
# To configure more than one peer, you will need to edit the `global.template.json` 
# file directly.
export PEER_PUBLIC_KEY="${PEER_PUBLIC_KEY:-$([ -f peer_public.key ] || (wg genkey | tee peer_private.key | wg pubkey > peer_public.key); cat peer_public.key)}"

# The shared secret to use for RADIUS authentication. This must match the value
# configured on the RADIUS client (e.g. your network access server). The default is
# a randomly generated 32-character string. To specify your own shared secret, set the
# `RADIUS_SHARED_SECRET` environment variable to the desired value.
export RADIUS_SHARED_SECRET="${RADIUS_SHARED_SECRET:-$([ -f radius_shared_secret.txt ] || (cat /dev/urandom | tr -dc 'a-zA-Z0-9' | fold -w 32 | head -n 1 > radius_shared_secret.txt); cat radius_shared_secret.txt)}"
