# exit when any command fails
set -ex

cfn-lint --non-zero-exit-code error templates/global.template.json
cfn-lint --non-zero-exit-code error templates/region.template.json
cfn-lint --non-zero-exit-code error templates/region-auth.template.json
cfn-lint --non-zero-exit-code error templates/region-acct.template.json
