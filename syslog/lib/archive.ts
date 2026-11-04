import { Duration, RemovalPolicy, Stack } from 'aws-cdk-lib';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as firehose from 'aws-cdk-lib/aws-kinesisfirehose';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as s3 from 'aws-cdk-lib/aws-s3';
import { Construct } from 'constructs';
import { SyslogConfig } from './config';
import { FirehoseSupport, secureBucketProps } from './firehose';
import { Names } from './names';

/**
 * The Object Lock (WORM) archive of the original message bytes, the archive Firehose stream
 * (gzip NDJSON, partitioned by arrival hour), and optional cross-region replication.
 */
export class Archive extends Construct {
  readonly bucket: s3.Bucket;
  readonly stream: firehose.CfnDeliveryStream;

  constructor(scope: Construct, id: string, config: SyslogConfig, names: Names, key: kms.IKey) {
    super(scope, id);
    const r = config.retention;
    const sec = config.security;
    const retention = Duration.days(r.archiveDays);

    this.bucket = new s3.Bucket(this, 'Bucket', {
      ...secureBucketProps(key),
      bucketName: names.archiveBucket,
      versioned: true,
      objectLockEnabled: true,
      // WARNING: COMPLIANCE retention can't be shortened or removed by anyone, including root.
      objectLockDefaultRetention: r.archiveLockMode === 'COMPLIANCE'
        ? s3.ObjectLockRetention.compliance(retention)
        : s3.ObjectLockRetention.governance(retention),
      lifecycleRules: [
        {
          id: 'ArchiveRetention',
          expiration: retention,
          transitions: r.archiveGlacierIrDays
            ? [{ storageClass: s3.StorageClass.GLACIER_INSTANT_RETRIEVAL, transitionAfter: Duration.days(r.archiveGlacierIrDays) }]
            : undefined,
          noncurrentVersionExpiration: Duration.days(1),
          abortIncompleteMultipartUploadAfter: Duration.days(7),
        },
        { id: 'CleanUpExpiredDeleteMarkers', expiredObjectDeleteMarker: true },
      ],
      // Object Lock keeps archived data after the stack is deleted anyway.
      removalPolicy: RemovalPolicy.RETAIN,
    });

    // Only the named principal may bypass Governance retention or change lock settings.
    // (No effect in COMPLIANCE mode, where nobody can shorten retention.)
    this.bucket.addToResourcePolicy(new iam.PolicyStatement({
      sid: 'OnlyBypassPrincipalChangesRetention',
      effect: iam.Effect.DENY,
      principals: [new iam.AnyPrincipal()],
      actions: ['s3:BypassGovernanceRetention', 's3:PutObjectRetention', 's3:PutBucketObjectLockConfiguration'],
      resources: [this.bucket.bucketArn, this.bucket.arnForObjects('*')],
      conditions: sec.governanceBypassPrincipalArn
        ? { ArnNotEquals: { 'aws:PrincipalArn': sec.governanceBypassPrincipalArn } }
        : undefined,
    }));

    if (sec.replicaBucketArn) this.addReplication(sec.replicaBucketArn, sec.replicaKmsKeyArn, key);

    const support = new FirehoseSupport(this, 'Firehose', {
      logGroupName: `${names.logGroupPrefix}/firehose-archive`,
      retentionDays: r.logGroupDays,
      key,
      logStreamNames: ['S3Delivery'],
    });
    support.allowWrite(this.bucket, key);

    this.stream = new firehose.CfnDeliveryStream(this, 'Stream', {
      deliveryStreamName: names.stream('archive'),
      deliveryStreamType: 'DirectPut',
      extendedS3DestinationConfiguration: {
        bucketArn: this.bucket.bucketArn,
        roleArn: support.role.roleArn,
        prefix: 'archive/dt=!{timestamp:yyyy-MM-dd}/hr=!{timestamp:HH}/',
        errorOutputPrefix: 'errors/!{firehose:error-output-type}/dt=!{timestamp:yyyy-MM-dd}/',
        bufferingHints: { intervalInSeconds: 300, sizeInMBs: 64 },
        compressionFormat: 'GZIP',
        encryptionConfiguration: { kmsEncryptionConfig: { awskmsKeyArn: key.keyArn } },
        cloudWatchLoggingOptions: support.loggingOptions('S3Delivery'),
      },
    });
    this.stream.node.addDependency(support.role);
  }

  private addReplication(replicaBucketArn: string, replicaKeyArn: string, key: kms.IKey): void {
    const region = Stack.of(this).region;
    const role = new iam.Role(this, 'ReplicationRole', {
      assumedBy: new iam.ServicePrincipal('s3.amazonaws.com', {
        conditions: { StringEquals: { 'aws:SourceAccount': Stack.of(this).account } },
      }),
    });
    role.addToPolicy(new iam.PolicyStatement({
      actions: ['s3:GetReplicationConfiguration', 's3:ListBucket'],
      resources: [this.bucket.bucketArn],
    }));
    role.addToPolicy(new iam.PolicyStatement({
      actions: [
        's3:GetObjectVersionForReplication', 's3:GetObjectVersionAcl', 's3:GetObjectVersionTagging',
        's3:GetObjectRetention', 's3:GetObjectLegalHold',
      ],
      resources: [this.bucket.arnForObjects('*')],
    }));
    role.addToPolicy(new iam.PolicyStatement({
      actions: ['s3:ReplicateObject', 's3:ReplicateTags'],
      resources: [`${replicaBucketArn}/*`],
    }));
    role.addToPolicy(new iam.PolicyStatement({
      actions: ['kms:Decrypt'],
      resources: [key.keyArn],
      conditions: {
        StringEquals: { 'kms:ViaService': `s3.${region}.amazonaws.com` },
        StringLike: { 'kms:EncryptionContext:aws:s3:arn': `${this.bucket.bucketArn}*` },
      },
    }));
    role.addToPolicy(new iam.PolicyStatement({
      actions: ['kms:Encrypt', 'kms:GenerateDataKey'],
      resources: [replicaKeyArn],
      conditions: { StringLike: { 'kms:EncryptionContext:aws:s3:arn': `${replicaBucketArn}*` } },
    }));

    const cfnBucket = this.bucket.node.defaultChild as s3.CfnBucket;
    cfnBucket.replicationConfiguration = {
      role: role.roleArn,
      rules: [{
        id: 'ReplicateArchive',
        status: 'Enabled',
        priority: 1,
        filter: { prefix: '' },
        deleteMarkerReplication: { status: 'Disabled' },
        sourceSelectionCriteria: { sseKmsEncryptedObjects: { status: 'Enabled' } },
        destination: {
          bucket: replicaBucketArn,
          storageClass: 'GLACIER_IR',
          encryptionConfiguration: { replicaKmsKeyId: replicaKeyArn },
        },
      }],
    };
  }
}
