import { Duration, RemovalPolicy, Stack } from 'aws-cdk-lib';
import * as athena from 'aws-cdk-lib/aws-athena';
import * as glue from 'aws-cdk-lib/aws-glue';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as firehose from 'aws-cdk-lib/aws-kinesisfirehose';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as s3 from 'aws-cdk-lib/aws-s3';
import { Construct } from 'constructs';
import { SyslogConfig } from './config';
import { FirehoseSupport, secureBucketProps } from './firehose';
import { Names } from './names';
import { SAVED_QUERIES } from './queries';

// Envelope columns shared by both tables (lowercase: Firehose's OpenX JSON deserializer and the
// JSON SerDe are case-insensitive).
const ENVELOPE_COLUMNS: glue.CfnTable.ColumnProperty[] = [
  { name: 'receivedat', type: 'string', comment: 'Proxylity receive time, UTC, yyyy-MM-ddTHH:mm:ss' },
  { name: 'ingressregion', type: 'string' },
  { name: 'remote', type: 'struct<ipaddress:string,port:int,peerkey:string>', comment: 'Outer endpoint and authenticated WireGuard key of the site' },
  { name: 'local', type: 'struct<domain:string,port:int,protocol:string>' },
  {
    name: 'inner',
    type: 'struct<protocol:int,version:int,sourceaddress:string,sourceport:int,destinationaddress:string,destinationport:int>',
    comment: 'Device address inside the tunnel, as the site router reports it',
  },
  { name: 'formatter', type: 'string' },
];

// The syslog formatter's fields, schema_version 1. The formatter only adds fields; add new ones here.
const SYSLOG_DATA_TYPE = 'struct<schema_version:int,parse_ok:boolean,format:string,pri:int,facility:int,' +
  'severity:int,facility_name:string,severity_name:string,version:int,timestamp:string,hostname:string,' +
  'app_name:string,proc_id:string,msg_id:string,structured_data:map<string,map<string,string>>,' +
  'message:string,raw:string>';

function projection(days: number, location: string): Record<string, string> {
  return {
    'projection.enabled': 'true',
    'projection.dt.type': 'date',
    'projection.dt.format': 'yyyy-MM-dd',
    'projection.dt.range': `NOW-${days}DAYS,NOW`,
    'projection.dt.interval': '1',
    'projection.dt.interval.unit': 'DAYS',
    'projection.hr.type': 'integer',
    'projection.hr.range': '0,23',
    'projection.hr.digits': '2',
    'storage.location.template': `${location}dt=\${dt}/hr=\${hr}/`,
  };
}

/**
 * The analytics bucket and Parquet Firehose stream, the Glue tables for the analytics and archive
 * data (partition projection, no crawlers), and the Athena workgroup with saved queries.
 */
export class Analytics extends Construct {
  readonly bucket: s3.Bucket;
  readonly stream: firehose.CfnDeliveryStream;
  readonly workGroup: athena.CfnWorkGroup;

  constructor(scope: Construct, id: string, config: SyslogConfig, names: Names, key: kms.IKey, archiveBucket: s3.IBucket) {
    super(scope, id);
    const r = config.retention;
    const account = Stack.of(this).account;

    this.bucket = new s3.Bucket(this, 'Bucket', {
      ...secureBucketProps(key),
      bucketName: names.analyticsBucket,
      lifecycleRules: [{
        id: 'AnalyticsRetention',
        expiration: Duration.days(r.analyticsDays),
        transitions: r.analyticsGlacierIrDays
          ? [{ storageClass: s3.StorageClass.GLACIER_INSTANT_RETRIEVAL, transitionAfter: Duration.days(r.analyticsGlacierIrDays) }]
          : undefined,
        abortIncompleteMultipartUploadAfter: Duration.days(7),
      }],
      // Deleting the stack fails until the bucket is emptied, which is deliberate.
      removalPolicy: RemovalPolicy.DESTROY,
    });

    const results = new s3.Bucket(this, 'AthenaResults', {
      ...secureBucketProps(key),
      bucketName: names.athenaResultsBucket,
      lifecycleRules: [{
        id: 'ExpireQueryResults',
        expiration: Duration.days(r.athenaResultsDays),
        abortIncompleteMultipartUploadAfter: Duration.days(1),
      }],
      removalPolicy: RemovalPolicy.DESTROY,
    });

    const database = new glue.CfnDatabase(this, 'Database', {
      catalogId: account,
      databaseInput: { name: names.glueDatabase, description: `Syslog analytics and archive tables for ${names.prefix}.` },
    });

    const analyticsLocation = `s3://${this.bucket.bucketName}/analytics/`;
    const table = new glue.CfnTable(this, 'AnalyticsTable', {
      catalogId: account,
      databaseName: names.glueDatabase,
      tableInput: {
        name: 'syslog',
        description: 'Parsed syslog messages (syslog formatter, schema_version 1) as Parquet, partitioned by Firehose arrival time (UTC).',
        tableType: 'EXTERNAL_TABLE',
        parameters: { classification: 'parquet', ...projection(r.analyticsDays, analyticsLocation) },
        partitionKeys: [{ name: 'dt', type: 'string' }, { name: 'hr', type: 'string' }],
        storageDescriptor: {
          location: analyticsLocation,
          inputFormat: 'org.apache.hadoop.hive.ql.io.parquet.MapredParquetInputFormat',
          outputFormat: 'org.apache.hadoop.hive.ql.io.parquet.MapredParquetOutputFormat',
          serdeInfo: {
            serializationLibrary: 'org.apache.hadoop.hive.ql.io.parquet.serde.ParquetHiveSerDe',
            parameters: { 'serialization.format': '1' },
          },
          columns: [
            ...ENVELOPE_COLUMNS,
            { name: 'data', type: SYSLOG_DATA_TYPE, comment: 'Parsed syslog fields; hostname and app_name are self-reported by the device' },
          ],
        },
      },
    });
    table.node.addDependency(database);

    const archiveLocation = `s3://${archiveBucket.bucketName}/archive/`;
    const archiveTable = new glue.CfnTable(this, 'ArchiveTable', {
      catalogId: account,
      databaseName: names.glueDatabase,
      tableInput: {
        name: 'syslog_archive',
        description: 'Envelope of the WORM archive (gzip NDJSON). data is the original message as base64; decode with from_utf8(from_base64(data)).',
        tableType: 'EXTERNAL_TABLE',
        parameters: { classification: 'json', compressionType: 'gzip', ...projection(r.archiveDays, archiveLocation) },
        partitionKeys: [{ name: 'dt', type: 'string' }, { name: 'hr', type: 'string' }],
        storageDescriptor: {
          location: archiveLocation,
          inputFormat: 'org.apache.hadoop.mapred.TextInputFormat',
          outputFormat: 'org.apache.hadoop.hive.ql.io.HiveIgnoreKeyTextOutputFormat',
          serdeInfo: {
            serializationLibrary: 'org.openx.data.jsonserde.JsonSerDe',
            parameters: { 'case.insensitive': 'true', 'ignore.malformed.json': 'false' },
          },
          columns: [...ENVELOPE_COLUMNS, { name: 'data', type: 'string', comment: 'Original message bytes, base64' }],
        },
      },
    });
    archiveTable.node.addDependency(database);

    const support = new FirehoseSupport(this, 'Firehose', {
      logGroupName: `${names.logGroupPrefix}/firehose-analytics`,
      retentionDays: r.logGroupDays,
      key,
      logStreamNames: ['S3Delivery'],
    });
    support.allowWrite(this.bucket, key);
    const { partition, region } = Stack.of(this);
    support.role.addToPolicy(new iam.PolicyStatement({
      sid: 'ReadConversionSchema',
      actions: ['glue:GetTable', 'glue:GetTableVersion', 'glue:GetTableVersions'],
      resources: [
        `arn:${partition}:glue:${region}:${account}:catalog`,
        `arn:${partition}:glue:${region}:${account}:database/${names.glueDatabase}`,
        `arn:${partition}:glue:${region}:${account}:table/${names.glueDatabase}/syslog`,
      ],
    }));

    this.stream = new firehose.CfnDeliveryStream(this, 'Stream', {
      deliveryStreamName: names.stream('analytics'),
      deliveryStreamType: 'DirectPut',
      extendedS3DestinationConfiguration: {
        bucketArn: this.bucket.bucketArn,
        roleArn: support.role.roleArn,
        prefix: 'analytics/dt=!{timestamp:yyyy-MM-dd}/hr=!{timestamp:HH}/',
        errorOutputPrefix: 'errors/!{firehose:error-output-type}/dt=!{timestamp:yyyy-MM-dd}/',
        // Parquet conversion requires a buffer of at least 64 MB.
        bufferingHints: { intervalInSeconds: 300, sizeInMBs: 64 },
        compressionFormat: 'UNCOMPRESSED',
        encryptionConfiguration: { kmsEncryptionConfig: { awskmsKeyArn: key.keyArn } },
        cloudWatchLoggingOptions: support.loggingOptions('S3Delivery'),
        dataFormatConversionConfiguration: {
          enabled: true,
          inputFormatConfiguration: { deserializer: { openXJsonSerDe: { caseInsensitive: true } } },
          outputFormatConfiguration: { serializer: { parquetSerDe: { compression: 'SNAPPY' } } },
          schemaConfiguration: {
            catalogId: account,
            databaseName: names.glueDatabase,
            tableName: 'syslog',
            region,
            roleArn: support.role.roleArn,
            versionId: 'LATEST',
          },
        },
      },
    });
    this.stream.node.addDependency(support.role, table);

    this.workGroup = new athena.CfnWorkGroup(this, 'WorkGroup', {
      name: names.prefix,
      description: `Syslog queries for ${names.prefix}.`,
      state: 'ENABLED',
      recursiveDeleteOption: true,
      workGroupConfiguration: {
        enforceWorkGroupConfiguration: true,
        publishCloudWatchMetricsEnabled: true,
        bytesScannedCutoffPerQuery: 100 * 1024 ** 3,
        engineVersion: { selectedEngineVersion: 'AUTO' },
        resultConfiguration: {
          outputLocation: `s3://${results.bucketName}/results/`,
          encryptionConfiguration: { encryptionOption: 'SSE_KMS', kmsKey: key.keyArn },
        },
      },
    });

    for (const q of SAVED_QUERIES) {
      const query = new athena.CfnNamedQuery(this, `Query${q.id}`, {
        name: q.name,
        description: q.description,
        database: names.glueDatabase,
        workGroup: this.workGroup.name,
        queryString: q.sql,
      });
      query.node.addDependency(this.workGroup, database);
    }
  }
}
