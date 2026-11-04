import { CfnOutput, Stack, StackProps } from 'aws-cdk-lib';
import { Construct } from 'constructs';
import { Analytics } from './analytics';
import { Archive } from './archive';
import { SyslogConfig } from './config';
import { Ingest } from './ingest';
import { Monitoring } from './monitoring';
import { Names } from './names';
import { Security } from './security';
import { Siem } from './siem';

export interface SyslogStackProps extends StackProps {
  readonly config: SyslogConfig;
}

/**
 * Syslog collection over WireGuard with Proxylity UDP Gateway: a WORM archive of the original
 * bytes, parsed events in CloudWatch Logs, Parquet analytics in Athena, alarms, a dashboard, and
 * optional SIEM forwarding.
 */
export class SyslogStack extends Stack {
  constructor(scope: Construct, id: string, props: SyslogStackProps) {
    super(scope, id, props);
    const { config } = props;
    const names = new Names(this);

    const security = new Security(this, 'Security', config, names);
    const archive = new Archive(this, 'Archive', config, names, security.key);
    const analytics = new Analytics(this, 'Analytics', config, names, security.key, archive.bucket);
    const ingest = new Ingest(this, 'Ingest', {
      config,
      names,
      key: security.key,
      archiveStream: archive.stream,
      analyticsStream: analytics.stream,
    });
    const monitoring = new Monitoring(this, 'Monitoring', {
      config,
      names,
      ingest,
      archiveStream: archive.stream,
      analyticsStream: analytics.stream,
    });
    const siem = config.siem.hecEndpoint
      ? new Siem(this, 'Siem', config, names, security.key, ingest.logsGroup)
      : undefined;

    const listener = ingest.listener;
    const output = (id: string, value: string, description: string) => new CfnOutput(this, id, { value, description });
    output('Domain', `${listener.domain}.proxylity.com`, 'Listener endpoint host name.');
    output('Port', listener.port, 'Listener endpoint port.');
    output('ListenerPublicKey', listener.wireGuardPublicKey, "WireGuard public key of the listener. Use it as PublicKey in each site's [Peer] section.");
    output('PeerConfig',
      `[Peer]\nPublicKey = ${listener.wireGuardPublicKey}\nEndpoint = ${listener.endpoint}\n` +
        `AllowedIPs = ${config.network.collectorAddress}/32\nPersistentKeepalive = 25\n`,
      "A ready-to-paste [Peer] block for each site's WireGuard configuration.");
    output('CollectorAddress', config.network.collectorAddress, 'Tunnel address that sites send syslog to (UDP/514).');
    output('ListenerName', names.prefix, 'Listener name (the Listener dimension of Proxylity metrics).');
    output('DashboardUrl',
      `https://${this.region}.console.aws.amazon.com/cloudwatch/home?region=${this.region}#dashboards/dashboard/${names.prefix}`,
      'CloudWatch dashboard for the pipeline.');
    output('AlarmTopicArn', monitoring.topic.topicArn, 'SNS topic for all alarms. Subscribe more endpoints as needed.');
    output('LogsGroupName', ingest.logsGroup.logGroupName, 'CloudWatch Logs group with one JSON event per syslog message.');
    output('ErrorsGroupName', ingest.errorsGroup.logGroupName, 'CloudWatch Logs group for Proxylity listener and destination errors.');
    output('MetricNamespace', names.metricNamespace, 'Namespace of the syslog content metrics (metric filters).');
    output('ArchiveBucketName', archive.bucket.bucketName, 'Object Lock bucket with the original messages (base64 NDJSON, gzip).');
    output('AnalyticsBucketName', analytics.bucket.bucketName, 'Bucket with the parsed messages as Parquet.');
    output('GlueDatabaseName', names.glueDatabase, 'Glue database with the syslog (Parquet) and syslog_archive (JSON) tables.');
    output('AthenaWorkGroup', analytics.workGroup.name, 'Athena workgroup with the saved queries.');
    output('KmsKeyArn', security.key.keyArn, 'Customer managed key for the buckets and log groups.');
    output('AuditorPolicyArn', security.auditorPolicy.managedPolicyArn, 'Managed policy for read-only auditors (query and read, no delete).');
    output('OperatorPolicyArn', security.operatorPolicy.managedPolicyArn, 'Managed policy for operators (dashboards, alarms, metrics, no data read).');
    if (siem) output('SiemStreamName', siem.stream.ref, 'Firehose stream forwarding events to Splunk HEC.');
  }
}
