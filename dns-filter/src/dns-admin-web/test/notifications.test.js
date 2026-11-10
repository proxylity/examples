import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const template = JSON.parse(readFileSync(new URL('../../../templates/dns-filter-region.template.json', import.meta.url), 'utf8'));
const admin = JSON.parse(readFileSync(new URL('../../../templates/dns-filter-admin.template.json', import.meta.url), 'utf8'));
const artifacts = JSON.parse(readFileSync(new URL('../../../templates/dns-filter-artifacts.template.json', import.meta.url), 'utf8'));

test('regional Lambda logs are stack scoped and each function uses its explicit group', () => {
  for (const [name, suffix] of [
    ['DnsAsnDataLambda', 'dns-asn-data'], ['DnsAnalysisLambda', 'dns-analysis'],
    ['DnsFilterLambda', 'dns-filter'], ['DnsApiAdapterLambda', 'dns-api-adapter']
  ]) {
    assert.deepEqual(template.Resources[`${name}LogGroup`].Properties.LogGroupName,
      { 'Fn::Sub': `/aws/lambda/\${AWS::StackName}/${suffix}` });
    assert.deepEqual(template.Resources[name].Properties.LoggingConfig.LogGroup, { Ref: `${name}LogGroup` });
  }
});

test('artifact policies authorize service reads and SSE-KMS decryption without public access', () => {
  const [, read] = artifacts.Resources.ArtifactBucketPolicy.Properties.PolicyDocument.Statement[1]['Fn::If'];
  assert.deepEqual(read.Principal, { Service: 'lambda.amazonaws.com' });
  assert.deepEqual(read.Action, ['s3:GetObject', 's3:GetObjectVersion']);
  assert.deepEqual(read.Resource, { 'Fn::Sub': '${Artifacts.Arn}/${AdminArtifactPrefix}*' });
  assert.deepEqual(read.Condition.ArnLike['aws:SourceArn'],
    { 'Fn::Sub': 'arn:${AWS::Partition}:lambda:${AWS::Region}:${AWS::AccountId}:web-function/${AdminFunctionName}/revision/*' });
  const [, decrypt] = artifacts.Resources.ArtifactKey.Properties.KeyPolicy.Statement[1]['Fn::If'];
  assert.deepEqual(decrypt.Principal, { Service: 'lambda.amazonaws.com' });
  assert.equal(decrypt.Action, 'kms:Decrypt');
  assert.deepEqual(decrypt.Condition.StringEquals['kms:ViaService'], { 'Fn::Sub': 's3.${AWS::Region}.amazonaws.com' });
  assert.deepEqual(decrypt.Condition.ArnLike['kms:EncryptionContext:aws:s3:arn'], [
    { 'Fn::Sub': 'arn:${AWS::Partition}:s3:::${BucketName}' },
    { 'Fn::Sub': 'arn:${AWS::Partition}:s3:::${BucketName}/${AdminArtifactPrefix}*' }
  ]);
  assert.equal(artifacts.Resources.Artifacts.Properties.PublicAccessBlockConfiguration.BlockPublicPolicy, true);
});

test('admin bootstrap always creates a revision and assigns endpoint traffic before its origin is known', () => {
  assert.equal(admin.Resources.AdminRevision.Condition, undefined);
  assert.deepEqual(admin.Resources.AdminEndpoint.Properties.RevisionWeights,
    [{ RevisionId: { 'Fn::GetAtt': ['AdminRevision', 'RevisionId'] }, Weight: 100 }]);
  assert.deepEqual(admin.Resources.AdminRevision.Properties.ServiceConfig.EnvironmentVariables.PUBLIC_URL,
    { 'Fn::If': ['HasPublicUrl', { Ref: 'PublicUrl' }, 'https://example.invalid'] });
  assert.deepEqual(admin.Resources.UserPoolClient.Properties.CallbackURLs,
    [{ 'Fn::If': ['HasPublicUrl', { 'Fn::Sub': '${PublicUrl}/callback' }, 'https://example.invalid/callback'] }]);
});

test('revision encryption key allows Lambda service decryption scoped to this function revisions', () => {
  const statement = admin.Resources.AdminKey.Properties.KeyPolicy.Statement.find(x => x.Sid === 'WebFunctionRevisionDecryption');
  assert.deepEqual(statement.Principal, { Service: 'lambda.amazonaws.com' });
  assert.equal(statement.Action, 'kms:Decrypt');
  const revisionArn = { 'Fn::Sub': 'arn:${AWS::Partition}:lambda:${AWS::Region}:${AWS::AccountId}:web-function/${AWS::StackName}/revision/*' };
  assert.deepEqual(statement.Condition.StringLike['aws:SourceArn'], revisionArn);
  assert.deepEqual(statement.Condition.StringLike['kms:EncryptionContext:aws:lambda:WebFunctionRevisionArn'], revisionArn);
});
function evaluate(value, params) {
  if (typeof value !== 'object') return value;
  if (value.Ref) return params[value.Ref];
  if (value.Condition) return evaluate(template.Conditions[value.Condition], params);
  if (value['Fn::Equals']) return value['Fn::Equals'].map(x => evaluate(x, params)).reduce((a, b) => a === b);
  if (value['Fn::Not']) return !evaluate(value['Fn::Not'][0], params);
  if (value['Fn::And']) return value['Fn::And'].every(x => evaluate(x, params));
  throw Error('Unsupported condition');
}

test('notification resources are absent without an email or query logging', () => {
  const resources = ['SuspiciousDomainTopic', 'SuspiciousDomainSubscription', 'SuspiciousDomainTopicPolicy', 'SuspiciousDomainEventRule'];
  for (const email of ['', 'alerts@example.com']) {
    for (const retention of [0, 30]) {
      const params = { SuspiciousDomainEmail: email, DnsLogRetentionDays: retention };
      for (const name of resources) {
        assert.equal(template.Resources[name].Condition, 'NotifySuspiciousDomains');
        assert.equal(evaluate({ Condition: template.Resources[name].Condition }, params), email !== '' && retention !== 0);
      }
    }
  }
  assert.equal(template.Parameters.SuspiciousDomainEmail.Default, '');
  assert.deepEqual(template.Resources.SuspiciousDomainTopic.Properties.KmsMasterKeyId, { Ref: 'RegionalKey' });
  assert.equal(template.Resources.SuspiciousDomainSubscription.Properties.Protocol, 'email');
});

test('notification text contains scalar reasons and review link, or explains no UI is configured', () => {
  const transformer = template.Resources.SuspiciousDomainEventRule.Properties.Targets[0].InputTransformer;
  const detail = { Domain: 'new.example', TotalQueries: 10, NxDomainCount: 5,
    ReasonSummary: 'High NXDOMAIN ratio', RulePath: '/rules?kind=domain&value=new.example' };
  const event = { detail, region: 'us-east-1', time: '2026-11-06T20:00:00Z' };
  const [, withUi, withoutUi] = transformer.InputTemplate['Fn::If'];
  for (const origin of ['', 'https://admin.example']) {
    const selected = evaluate({ Condition: 'HasAdminUrl' }, { AdminUrl: origin }) ? withUi['Fn::Sub'].replace('${AdminUrl}', origin) : withoutUi;
    const rendered = selected.replace(/<(\w+)>/g, (_, name) => {
      const value = transformer.InputPathsMap[name].slice(2).split('.').reduce((v, key) => v[key], event);
      assert.ok(['string', 'number'].includes(typeof value));
      return String(value);
    });
    const text = JSON.parse(rendered);
    assert.ok(text.includes('Reasons: High NXDOMAIN ratio'));
    assert.ok(text.includes('new.example'));
    assert.ok(!text.includes('<domain>'));
    if (origin) assert.ok(text.includes('https://admin.example/rules?kind=domain&value=new.example'));
    else assert.ok(text.includes('admin UI link is not configured'));
  }
});
