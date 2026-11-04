#!/usr/bin/env node
import { App, Token } from 'aws-cdk-lib';
import * as path from 'path';
import { loadConfig } from '../lib/config';
import { SyslogStack } from '../lib/syslog-stack';

// Select the stack name and config directory with context, for example
//   cdk deploy -c stackName=syslog-prod -c configDir=./env/prod
const app = new App();
const stackName: string = app.node.tryGetContext('stackName') ?? 'syslog';
const configDir = path.resolve(app.node.tryGetContext('configDir') ?? '.');
const region = process.env.CDK_DEPLOY_REGION ?? process.env.CDK_DEFAULT_REGION;
const account = process.env.CDK_DEPLOY_ACCOUNT ?? process.env.CDK_DEFAULT_ACCOUNT;

// Names include the region, so it must be known at synth time.
if (!region || Token.isUnresolved(region)) {
  throw new Error('Set a region (AWS_REGION, a profile region, or CDK_DEPLOY_REGION) before synthesizing.');
}

const config = loadConfig(configDir, { stackName, region });

new SyslogStack(app, stackName, {
  stackName,
  env: { account, region },
  description: 'Syslog collection over WireGuard with Proxylity UDP Gateway (proxylity examples/syslog).',
  config,
});
