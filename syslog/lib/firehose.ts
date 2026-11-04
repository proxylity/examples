import { RemovalPolicy, Stack } from 'aws-cdk-lib';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as s3 from 'aws-cdk-lib/aws-s3';
import { Construct } from 'constructs';

/** The delivery role and error log group every Firehose stream in the example needs. */
export class FirehoseSupport extends Construct {
  readonly role: iam.Role;
  readonly logGroup: logs.LogGroup;
  readonly logStreams: Record<string, logs.LogStream> = {};

  constructor(scope: Construct, id: string, props: {
    logGroupName: string;
    retentionDays: number;
    key: kms.IKey;
    logStreamNames: string[];
  }) {
    super(scope, id);
    const { partition, region, account } = Stack.of(this);

    this.logGroup = new logs.LogGroup(this, 'LogGroup', {
      logGroupName: props.logGroupName,
      retention: props.retentionDays as logs.RetentionDays,
      encryptionKey: props.key,
      removalPolicy: RemovalPolicy.DESTROY,
    });
    for (const name of props.logStreamNames) {
      this.logStreams[name] = new logs.LogStream(this, name, {
        logGroup: this.logGroup,
        logStreamName: name,
        removalPolicy: RemovalPolicy.DESTROY,
      });
    }

    this.role = new iam.Role(this, 'Role', {
      assumedBy: new iam.ServicePrincipal('firehose.amazonaws.com', {
        conditions: { StringEquals: { 'sts:ExternalId': account } },
      }),
    });
    this.role.addToPolicy(new iam.PolicyStatement({
      sid: 'DeliveryErrorLogs',
      actions: ['logs:PutLogEvents'],
      resources: [`arn:${partition}:logs:${region}:${account}:log-group:${this.logGroup.logGroupName}:log-stream:*`],
    }));
  }

  /** Lets the stream write (and encrypt) objects in one bucket. */
  allowWrite(bucket: s3.IBucket, key: kms.IKey): void {
    const region = Stack.of(this).region;
    this.role.addToPolicy(new iam.PolicyStatement({
      sid: 'WriteBucket',
      actions: [
        's3:AbortMultipartUpload', 's3:GetBucketLocation', 's3:GetObject', 's3:ListBucket',
        's3:ListBucketMultipartUploads', 's3:PutObject',
      ],
      resources: [bucket.bucketArn, bucket.arnForObjects('*')],
    }));
    this.role.addToPolicy(new iam.PolicyStatement({
      sid: 'EncryptObjects',
      actions: ['kms:GenerateDataKey', 'kms:Decrypt'],
      resources: [key.keyArn],
      conditions: {
        StringEquals: { 'kms:ViaService': `s3.${region}.amazonaws.com` },
        StringLike: { 'kms:EncryptionContext:aws:s3:arn': [bucket.bucketArn, bucket.arnForObjects('*')] },
      },
    }));
  }

  loggingOptions(streamName: string) {
    return {
      enabled: true,
      logGroupName: this.logGroup.logGroupName,
      logStreamName: this.logStreams[streamName].logStreamName,
    };
  }
}

/** Bucket settings shared by every bucket in the example. */
export function secureBucketProps(key: kms.IKey): Partial<s3.BucketProps> {
  return {
    encryption: s3.BucketEncryption.KMS,
    encryptionKey: key,
    bucketKeyEnabled: true,
    enforceSSL: true,
    blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
    objectOwnership: s3.ObjectOwnership.BUCKET_OWNER_ENFORCED,
  };
}
