import { randomUUID } from 'node:crypto';
import { DynamoDBClient } from '@aws-sdk/client-dynamodb';
import { DynamoDBDocumentClient, GetCommand, ScanCommand, QueryCommand, TransactWriteCommand } from '@aws-sdk/lib-dynamodb';
import { SecretsManagerClient, GetSecretValueCommand } from '@aws-sdk/client-secrets-manager';
import { CodeBuildClient, StartBuildCommand } from '@aws-sdk/client-codebuild';
import * as oidc from 'openid-client';
import { snapshotCondition, ruleAfter } from './policy.js';

export function services(config, overrides = {}) {
  const region = config.HOME_REGION;
  const db = overrides.db || DynamoDBDocumentClient.from(new DynamoDBClient({ region }), { marshallOptions: { removeUndefinedValues: true } });
  const secrets = overrides.secrets || new SecretsManagerClient({ region });
  const builds = overrides.builds || new CodeBuildClient({ region });
  let secretPromise, oidcPromise;
  return {
    secret() {
      if (!secretPromise) secretPromise = secrets.send(new GetSecretValueCommand({ SecretId: config.SESSION_SECRET_ARN })).then(r => {
        const s = r.SecretString ?? Buffer.from(r.SecretBinary || []).toString('utf8');
        if (s.length < 32 || Buffer.byteLength(s) < 32) throw Error('Session secret must be at least 32 characters of high entropy');
        return s;
      }).catch(e => { secretPromise = null; throw e; });
      return secretPromise;
    },
    oidc() {
      if (!oidcPromise) oidcPromise = oidc.discovery(new URL(config.COGNITO_ISSUER), config.COGNITO_CLIENT_ID, undefined, oidc.None()).then(c => {
        const metadata = c.serverMetadata();
        if (new URL(metadata.authorization_endpoint).origin !== new URL(config.COGNITO_DOMAIN).origin) throw Error('Cognito authorization endpoint does not match configured domain');
        oidc.enableNonRepudiationChecks(c);
        return c;
      }).catch(e => { oidcPromise = null; throw e; });
      return oidcPromise;
    },
    async loginUrl(redirectUri) {
      const c = await this.oidc();
      const verifier = oidc.randomPKCECodeVerifier(), state = oidc.randomState(), nonce = oidc.randomNonce();
      const url = oidc.buildAuthorizationUrl(c, { redirect_uri: redirectUri, scope: 'openid email', response_type: 'code', code_challenge_method: 'S256', code_challenge: await oidc.calculatePKCECodeChallenge(verifier), state, nonce });
      return { url: url.href, transaction: { verifier, state, nonce } };
    },
    async callback(url, transaction) {
      const c = await this.oidc();
      const tokens = await oidc.authorizationCodeGrant(c, url, { pkceCodeVerifier: transaction.verifier, expectedState: transaction.state, expectedNonce: transaction.nonce, idTokenExpected: true });
      return tokens.claims();
    },
    async get(key) {
      return (await db.send(new GetCommand({ TableName: config.TABLE_NAME, Key: { PK: key, SK: key }, ConsistentRead: true }))).Item;
    },
    async rules(cursor, kind) {
      const filters = {
        domain: 'PK = SK AND NOT begins_with(PK, :ip) AND NOT begins_with(PK, :asn)',
        ip: 'PK = SK AND begins_with(PK, :ip)',
        asn: 'PK = SK AND begins_with(PK, :asn)'
      };
      if (!Object.hasOwn(filters, kind)) throw Error('Invalid rule type');
      return db.send(new ScanCommand({
        TableName: config.TABLE_NAME, Limit: 50, ExclusiveStartKey: cursor, ConsistentRead: true,
        FilterExpression: filters[kind],
        ExpressionAttributeValues: kind === 'domain' ? { ':ip': 'IP#', ':asn': 'AS#' } :
          kind === 'ip' ? { ':ip': 'IP#' } : { ':asn': 'AS#' }
      }));
    },
    async audit(cursor) {
      return db.send(new QueryCommand({ TableName: config.TABLE_NAME, KeyConditionExpression: 'PK = :pk', ExpressionAttributeValues: { ':pk': 'AUDIT' }, Limit: 50, ExclusiveStartKey: cursor, ScanIndexForward: false, ConsistentRead: true }));
    },
    async change(key, before, action, actor, redirect) {
      const after = ruleAfter(before, key, action, redirect);
      const condition = snapshotCondition(before);
      const audit = { PK: 'AUDIT', SK: `${new Date().toISOString()}#${randomUUID()}`, actor, target: key, action, before: before || null, after };
      const operation = after ? { Put: { TableName: config.TABLE_NAME, Item: after, ...condition } } : { Delete: { TableName: config.TABLE_NAME, Key: { PK: key, SK: key }, ...condition } };
      await db.send(new TransactWriteCommand({ TransactItems: [operation, { Put: { TableName: config.TABLE_NAME, Item: audit, ConditionExpression: 'attribute_not_exists(PK) AND attribute_not_exists(SK)' } }] }));
    },
    async startBuild(actor, rollback) {
      const requestId = randomUUID();
      const record = async (status, detail) => db.send(new TransactWriteCommand({ TransactItems: [{ Put: {
        TableName: config.TABLE_NAME, Item: { PK: 'AUDIT', SK: `${new Date().toISOString()}#${randomUUID()}`,
          actor, target: config.BUILD_PROJECT, action: rollback ? 'rollback' : 'sync', requestId, status, detail: detail || null },
        ConditionExpression: 'attribute_not_exists(PK) AND attribute_not_exists(SK)'
      } }] }));
      await record('requested', rollback);
      let result;
      try {
        result = await builds.send(new StartBuildCommand({ projectName: config.BUILD_PROJECT,
          ...(rollback ? { environmentVariablesOverride: [
            { name: 'ROLLBACK_SOURCE', value: rollback.source, type: 'PLAINTEXT' },
            { name: 'ROLLBACK_COMMIT', value: rollback.commit, type: 'PLAINTEXT' }
          ] } : {}) }));
      } catch (error) {
        await record('failed', { error: error.name });
        throw error;
      }
      await record('accepted', { buildId: result.build?.id });
      return result;
    }
  };
}
