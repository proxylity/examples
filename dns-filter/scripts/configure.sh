set -x -e

#
# C O N F I G U R A T I O N
#

# Security first! To restrict use of the UDP and DoH services to a specific set of IP
# addresses, set the `ALLOWED_IPS` environment variable to the CIDR notation of the
# allowed IP addresses. The default is to restrict access to the current public IP (
# probably your internet gateway, so it will only be accessible from your network).
# To allow open/unrestricted access, set this to 0.0.0.0/0.
export ALLOWED_IPS="${ALLOWED_IPS:-$(curl -s checkip.amazonaws.com)/32}"

# The regions that will host handlers for the dns-filter example. The global
# stack will be deployed to us-west-2. 
export DEPLOY_TO_REGIONS="us-west-2 us-east-1 eu-west-1"

# The prefix of the bucket name to use for deployment artifacts. This will be suffixed
# with the region name, and a bucket with the resuling name must exist in each deployment
# region (due to the way CloudFormation/SAM works).   
export DEPLOY_BUCKET_NAME_PREFIX="cpdev-"

# The S3 path prefix to use for deployment artifacts. The `cloudformation deploy` and
# `sam deploy` commands will use this prefix to store artifacts used in the respective
# stacks.
export DEPLOY_BUCKET_PATH_PREFIX="builds/"

# The name of the stack to deploy.
export STACK_NAME="dns-filter"

# Optionally, to set a custom domain name for DNS over HTTPS endpoint uncomment the line
# below and set the domain name you want to use.  The domain will need to be registered
# in Route53 in the account you are deploying this example.  Also provide the hosted zone ID
# for the domain.  The subdomain "doh" will be registered as a record set in the hosted zone.
# export DOMAIN_NAME=""
# export HOSTED_ZONE_ID=""

# Optionally, set the log retention period in days. The default is 30 days.  If you want
# to disable logging entirely, set this to 0.
# export DNS_LOG_RETENTION="0"
