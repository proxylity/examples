import { Duration, RemovalPolicy, Stack } from 'aws-cdk-lib';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as firehose from 'aws-cdk-lib/aws-kinesisfirehose';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as s3 from 'aws-cdk-lib/aws-s3';
import { Construct } from 'constructs';
import { SyslogConfig } from './config';
import { FirehoseSupport, secureBucketProps } from './firehose';
import { Names } from './names';

/**
 * Optional: forwards the JSON syslog events in CloudWatch Logs to Splunk HEC through a subscription
 * filter and Firehose, with failed events backed up to S3. For other SIEMs, swap the Splunk
 * destination for an HTTP endpoint destination.
 */
export class Siem extends Construct {
  readonly stream: firehose.CfnDeliveryStream;

  constructor(scope: Construct, id: string, config: SyslogConfig, names: Names, key: kms.IKey, logsGroup: logs.ILogGroup) {
    super(scope, id);
    const siem = config.siem;

    const backup = new s3.Bucket(this, 'Backup', {
      ...secureBucketProps(key),
      bucketName: names.siemBackupBucket,
      lifecycleRules: [{
        id: 'ExpireFailedEvents',
        expiration: Duration.days(30),
        abortIncompleteMultipartUploadAfter: Duration.days(1),
      }],
      removalPolicy: RemovalPolicy.DESTROY,
    });

    const support = new FirehoseSupport(this, 'Firehose', {
      logGroupName: `${names.logGroupPrefix}/firehose-siem`,
      retentionDays: config.retention.logGroupDays,
      key,
      logStreamNames: ['SplunkDelivery', 'BackupDelivery'],
    });
    support.allowWrite(backup, key);
    support.role.addToPolicy(new iam.PolicyStatement({
      sid: 'ReadHecToken',
      actions: ['secretsmanager:GetSecretValue'],
      resources: [siem.hecTokenSecretArn],
    }));

    this.stream = new firehose.CfnDeliveryStream(this, 'Stream', {
      deliveryStreamName: names.stream('siem'),
      deliveryStreamType: 'DirectPut',
      splunkDestinationConfiguration: {
        hecEndpoint: siem.hecEndpoint,
        hecEndpointType: siem.hecEndpointType,
        secretsManagerConfiguration: { enabled: true, secretArn: siem.hecTokenSecretArn, roleArn: support.role.roleArn },
        hecAcknowledgmentTimeoutInSeconds: 180,
        retryOptions: { durationInSeconds: 300 },
        bufferingHints: { intervalInSeconds: 60, sizeInMBs: 5 },
        // Subscription filters deliver gzip batches; unpack them into one event per syslog message.
        processingConfiguration: {
          enabled: true,
          processors: [
            { type: 'Decompression', parameters: [{ parameterName: 'CompressionFormat', parameterValue: 'GZIP' }] },
            { type: 'CloudWatchLogProcessing', parameters: [{ parameterName: 'DataMessageExtraction', parameterValue: 'true' }] },
          ],
        },
        cloudWatchLoggingOptions: support.loggingOptions('SplunkDelivery'),
        s3BackupMode: 'FailedEventsOnly',
        s3Configuration: {
          bucketArn: backup.bucketArn,
          roleArn: support.role.roleArn,
          prefix: 'failed/',
          compressionFormat: 'GZIP',
          bufferingHints: { intervalInSeconds: 300, sizeInMBs: 5 },
          encryptionConfiguration: { kmsEncryptionConfig: { awskmsKeyArn: key.keyArn } },
          cloudWatchLoggingOptions: support.loggingOptions('BackupDelivery'),
        },
      },
    });
    this.stream.node.addDependency(support.role);

    const { partition, region, account } = Stack.of(this);
    const subscriptionRole = new iam.Role(this, 'SubscriptionRole', {
      assumedBy: new iam.ServicePrincipal('logs.amazonaws.com', {
        conditions: { StringLike: { 'aws:SourceArn': `arn:${partition}:logs:${region}:${account}:*` } },
      }),
    });
    subscriptionRole.addToPolicy(new iam.PolicyStatement({
      actions: ['firehose:PutRecord', 'firehose:PutRecordBatch'],
      resources: [this.stream.attrArn],
    }));

    const filter = new logs.CfnSubscriptionFilter(this, 'SubscriptionFilter', {
      filterName: `${names.prefix}-siem`,
      logGroupName: logsGroup.logGroupName,
      filterPattern: siem.subscriptionFilterPattern,
      destinationArn: this.stream.attrArn,
      roleArn: subscriptionRole.roleArn,
    });
    filter.node.addDependency(subscriptionRole);
  }
}
