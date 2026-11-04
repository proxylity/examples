import { Duration, RemovalPolicy, Stack } from 'aws-cdk-lib';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as kms from 'aws-cdk-lib/aws-kms';
import { Construct } from 'constructs';
import { SyslogConfig } from './config';
import { Names } from './names';

/**
 * The customer managed key for the buckets and log groups, and the SyslogAuditor and
 * SyslogOperator managed policies. Policies are built from physical names, so they don't depend on
 * the resources they cover.
 */
export class Security extends Construct {
  readonly key: kms.Key;
  readonly auditorPolicy: iam.ManagedPolicy;
  readonly operatorPolicy: iam.ManagedPolicy;

  constructor(scope: Construct, id: string, config: SyslogConfig, names: Names) {
    super(scope, id);
    const { partition, region, account } = Stack.of(this);
    const s3Arn = (bucket: string) => `arn:${partition}:s3:::${bucket}`;
    const logGroups = `arn:${partition}:logs:${region}:${account}:log-group:${names.logGroupPrefix}/*`;

    // The L2 key policy keeps the account-root statement: it's what lets IAM policies manage the key
    // at all, so a key policy mistake can't make the key (and the archive) unmanageable.
    this.key = new kms.Key(this, 'Key', {
      description: `Syslog archive, analytics and log group encryption for ${names.prefix}.`,
      alias: `alias/${names.prefix}-syslog`,
      enableKeyRotation: true,
      rotationPeriod: Duration.days(config.retention.keyRotationDays),
      admins: config.security.keyAdminPrincipalArns.map((arn) => new iam.ArnPrincipal(arn)),
      // The retained archive needs the key.
      removalPolicy: RemovalPolicy.RETAIN,
    });

    this.key.addToResourcePolicy(new iam.PolicyStatement({
      sid: 'CloudWatchLogsUseForSyslogLogGroups',
      principals: [new iam.ServicePrincipal(`logs.${region}.amazonaws.com`)],
      actions: ['kms:Encrypt*', 'kms:Decrypt*', 'kms:ReEncrypt*', 'kms:GenerateDataKey*', 'kms:Describe*'],
      resources: ['*'],
      conditions: { ArnLike: { 'kms:EncryptionContext:aws:logs:arn': logGroups } },
    }));
    const useActions = ['kms:Encrypt', 'kms:Decrypt', 'kms:ReEncrypt*', 'kms:GenerateDataKey*', 'kms:DescribeKey'];
    this.key.addToResourcePolicy(new iam.PolicyStatement({
      sid: 'S3UseForSyslogBuckets',
      principals: [new iam.ServicePrincipal('s3.amazonaws.com')],
      actions: useActions,
      resources: ['*'],
      conditions: {
        StringEquals: { 'aws:SourceAccount': account },
        ArnLike: {
          'aws:SourceArn': [names.archiveBucket, names.analyticsBucket, names.athenaResultsBucket, names.siemBackupBucket].map(s3Arn),
        },
      },
    }));
    this.key.addToResourcePolicy(new iam.PolicyStatement({
      sid: 'FirehoseUseForSyslogStreams',
      principals: [new iam.ServicePrincipal('firehose.amazonaws.com')],
      actions: useActions,
      resources: ['*'],
      conditions: {
        StringEquals: { 'aws:SourceAccount': account },
        ArnLike: { 'aws:SourceArn': `arn:${partition}:firehose:${region}:${account}:deliverystream/${names.prefix}-*` },
      },
    }));

    const dataBuckets = [names.archiveBucket, names.analyticsBucket].flatMap((b) => [s3Arn(b), `${s3Arn(b)}/*`]);

    this.auditorPolicy = new iam.ManagedPolicy(this, 'AuditorPolicy', {
      description: `SyslogAuditor for ${names.prefix}: query and read syslog data (Athena, Glue, S3, Logs Insights). Can't delete or change retention.`,
      statements: [
        new iam.PolicyStatement({
          sid: 'AthenaWorkGroup',
          actions: [
            'athena:StartQueryExecution', 'athena:StopQueryExecution', 'athena:GetQueryExecution',
            'athena:GetQueryResults', 'athena:GetQueryResultsStream', 'athena:GetQueryRuntimeStatistics',
            'athena:BatchGetQueryExecution', 'athena:ListQueryExecutions', 'athena:GetWorkGroup',
            'athena:ListNamedQueries', 'athena:GetNamedQuery', 'athena:BatchGetNamedQuery',
          ],
          resources: [`arn:${partition}:athena:${region}:${account}:workgroup/${names.prefix}`],
        }),
        new iam.PolicyStatement({
          sid: 'AthenaDiscovery',
          actions: ['athena:ListWorkGroups', 'athena:ListDataCatalogs', 'athena:ListEngineVersions'],
          resources: ['*'],
        }),
        new iam.PolicyStatement({
          sid: 'GlueCatalogRead',
          actions: [
            'glue:GetDatabase', 'glue:GetDatabases', 'glue:GetTable', 'glue:GetTables',
            'glue:GetPartition', 'glue:GetPartitions', 'glue:BatchGetPartition',
          ],
          resources: [
            `arn:${partition}:glue:${region}:${account}:catalog`,
            `arn:${partition}:glue:${region}:${account}:database/${names.glueDatabase}`,
            `arn:${partition}:glue:${region}:${account}:table/${names.glueDatabase}/*`,
          ],
        }),
        new iam.PolicyStatement({
          sid: 'ReadSyslogBuckets',
          actions: [
            's3:GetObject', 's3:GetObjectVersion', 's3:GetObjectRetention', 's3:GetObjectLegalHold',
            's3:ListBucket', 's3:ListBucketVersions', 's3:GetBucketLocation',
            's3:GetBucketObjectLockConfiguration', 's3:GetLifecycleConfiguration',
          ],
          resources: dataBuckets,
        }),
        new iam.PolicyStatement({
          sid: 'AthenaQueryResults',
          actions: [
            's3:GetObject', 's3:PutObject', 's3:ListBucket', 's3:GetBucketLocation',
            's3:AbortMultipartUpload', 's3:ListMultipartUploadParts',
          ],
          resources: [s3Arn(names.athenaResultsBucket), `${s3Arn(names.athenaResultsBucket)}/*`],
        }),
        new iam.PolicyStatement({
          sid: 'UseKeyToReadDataAndWriteResults',
          actions: ['kms:Decrypt', 'kms:GenerateDataKey', 'kms:DescribeKey'],
          resources: [this.key.keyArn],
        }),
        new iam.PolicyStatement({
          sid: 'LogsInsightsOnSyslogGroups',
          actions: ['logs:StartQuery', 'logs:GetLogEvents', 'logs:FilterLogEvents', 'logs:DescribeLogStreams', 'logs:GetLogGroupFields'],
          resources: [logGroups, `${logGroups}:*`],
        }),
        new iam.PolicyStatement({
          sid: 'LogsInsightsResults',
          actions: ['logs:GetQueryResults', 'logs:StopQuery', 'logs:DescribeQueries', 'logs:DescribeLogGroups', 'logs:GetLogRecord'],
          resources: ['*'],
        }),
        new iam.PolicyStatement({
          sid: 'AuditorsNeverDeleteOrChangeRetention',
          effect: iam.Effect.DENY,
          actions: [
            's3:DeleteObject', 's3:DeleteObjectVersion', 's3:PutObjectRetention', 's3:PutObjectLegalHold',
            's3:BypassGovernanceRetention', 's3:PutBucketObjectLockConfiguration', 's3:PutLifecycleConfiguration',
            's3:PutBucketPolicy', 's3:DeleteBucketPolicy', 's3:DeleteBucket',
          ],
          resources: dataBuckets,
        }),
        new iam.PolicyStatement({
          sid: 'AuditorsNeverChangeCatalogOrLogs',
          effect: iam.Effect.DENY,
          actions: [
            'glue:Delete*', 'glue:Update*', 'glue:Create*', 'glue:BatchDelete*',
            'logs:DeleteLogGroup', 'logs:DeleteLogStream', 'logs:PutRetentionPolicy', 'logs:DeleteRetentionPolicy',
            'logs:DisassociateKmsKey', 'athena:DeleteWorkGroup', 'athena:UpdateWorkGroup',
            'kms:ScheduleKeyDeletion', 'kms:DisableKey', 'kms:PutKeyPolicy',
          ],
          resources: ['*'],
        }),
      ],
    });

    this.operatorPolicy = new iam.ManagedPolicy(this, 'OperatorPolicy', {
      description: `SyslogOperator for ${names.prefix}: dashboards, alarms and metrics. No access to syslog content.`,
      statements: [
        new iam.PolicyStatement({
          sid: 'DashboardsAndMetrics',
          actions: [
            'cloudwatch:GetDashboard', 'cloudwatch:ListDashboards', 'cloudwatch:GetMetricData',
            'cloudwatch:GetMetricStatistics', 'cloudwatch:GetMetricWidgetImage', 'cloudwatch:ListMetrics',
            'cloudwatch:DescribeAlarms', 'cloudwatch:DescribeAlarmHistory', 'cloudwatch:DescribeAlarmsForMetric',
            'cloudwatch:DescribeInsightRules', 'cloudwatch:GetInsightRuleReport',
          ],
          resources: ['*'],
        }),
        new iam.PolicyStatement({
          sid: 'OperateSyslogAlarms',
          actions: ['cloudwatch:EnableAlarmActions', 'cloudwatch:DisableAlarmActions', 'cloudwatch:SetAlarmState'],
          resources: [`arn:${partition}:cloudwatch:${region}:${account}:alarm:${names.prefix}-*`],
        }),
        new iam.PolicyStatement({
          sid: 'PipelineStatus',
          actions: [
            'firehose:DescribeDeliveryStream', 'firehose:ListDeliveryStreams', 'sns:ListTopics',
            'sns:GetTopicAttributes', 'sns:ListSubscriptionsByTopic', 'logs:DescribeLogGroups',
            'logs:DescribeMetricFilters', 'logs:DescribeSubscriptionFilters',
          ],
          resources: ['*'],
        }),
        new iam.PolicyStatement({
          sid: 'OperatorsNeverReadSyslogContent',
          effect: iam.Effect.DENY,
          actions: [
            's3:GetObject', 's3:GetObjectVersion', 'logs:GetLogEvents', 'logs:FilterLogEvents',
            'logs:StartQuery', 'logs:StartLiveTail', 'logs:GetLogRecord', 'athena:StartQueryExecution',
            'athena:GetQueryResults', 'kms:Decrypt',
          ],
          resources: ['*'],
        }),
      ],
    });
  }
}
