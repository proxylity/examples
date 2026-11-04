import {
  CloudWatchLogsDestination,
  FirehoseDestination,
  Formatter,
  Listener,
  ListenerProtocol,
  ProxylityConfig,
} from '@proxylity/cdk-udp-gateway';
import { RemovalPolicy, Stack } from 'aws-cdk-lib';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as firehose from 'aws-cdk-lib/aws-kinesisfirehose';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as logs from 'aws-cdk-lib/aws-logs';
import { Construct } from 'constructs';
import { SyslogConfig } from './config';
import { Names } from './names';

const PROXYLITY_METRICS_NAMESPACE = 'Proxylity/UDP Gateway';

/**
 * The WireGuard listener and its three destinations, the encrypted logs and errors log groups, the
 * syslog metric filters, and the Contributor Insights rules.
 */
export class Ingest extends Construct {
  readonly listener: Listener;
  readonly logsGroup: logs.LogGroup;
  readonly errorsGroup: logs.LogGroup;
  readonly metrics: {
    messages: cloudwatch.Metric;
    highSeverity: cloudwatch.Metric;
    parseFailures: cloudwatch.Metric;
    severity: (name: string) => cloudwatch.Metric;
    site: (publicKey: string) => cloudwatch.Metric;
  };

  constructor(scope: Construct, id: string, props: {
    config: SyslogConfig;
    names: Names;
    key: kms.IKey;
    archiveStream: firehose.CfnDeliveryStream;
    analyticsStream: firehose.CfnDeliveryStream;
  }) {
    super(scope, id);
    const { config, names, key } = props;
    const retention = config.retention.logGroupDays as logs.RetentionDays;

    this.logsGroup = new logs.LogGroup(this, 'Logs', {
      logGroupName: `${names.logGroupPrefix}/logs`,
      retention,
      encryptionKey: key,
      removalPolicy: RemovalPolicy.DESTROY,
    });
    this.errorsGroup = new logs.LogGroup(this, 'Errors', {
      logGroupName: `${names.logGroupPrefix}/errors`,
      retention,
      encryptionKey: key,
      removalPolicy: RemovalPolicy.DESTROY,
    });

    // One delivery role for all three destinations, and a separate observability role for the
    // listener. Both are passed to the L2 constructs explicitly so their grants stay narrow:
    // metrics limited to the Proxylity namespace, log writes limited to these two groups.
    const proxylity = ProxylityConfig.forStack(this);
    const metricsStatement = new iam.PolicyStatement({
      sid: 'PublishProxylityMetrics',
      actions: ['cloudwatch:PutMetricData'],
      resources: ['*'],
      conditions: { StringEquals: { 'cloudwatch:namespace': PROXYLITY_METRICS_NAMESPACE } },
    });
    const streams = (group: logs.ILogGroup) => `${group.logGroupArn}:*`;

    const destinationRole = new iam.Role(this, 'DestinationRole', {
      assumedBy: new iam.ArnPrincipal(proxylity.serviceRole()),
      description: `Proxylity delivery for the ${names.prefix} syslog destinations.`,
    });
    destinationRole.addToPolicy(new iam.PolicyStatement({
      sid: 'DeliverToStreams',
      actions: ['firehose:PutRecord', 'firehose:PutRecordBatch'],
      resources: [props.archiveStream.attrArn, props.analyticsStream.attrArn],
    }));
    destinationRole.addToPolicy(new iam.PolicyStatement({
      sid: 'DeliverToLogsAndReportErrors',
      actions: ['logs:CreateLogStream', 'logs:PutLogEvents'],
      resources: [streams(this.logsGroup), streams(this.errorsGroup)],
    }));
    destinationRole.addToPolicy(metricsStatement);

    const listenerRole = new iam.Role(this, 'ListenerRole', {
      assumedBy: new iam.ArnPrincipal(proxylity.serviceRole()),
      description: `Proxylity metrics and error logs for the ${names.prefix} listener.`,
    });
    listenerRole.addToPolicy(new iam.PolicyStatement({
      sid: 'ReportErrors',
      actions: ['logs:CreateLogStream', 'logs:PutLogEvents'],
      resources: [streams(this.errorsGroup)],
    }));
    listenerRole.addToPolicy(metricsStatement);

    const common = {
      role: destinationRole,
      metricsEnabled: true,
      logGroupName: this.errorsGroup.logGroupName,
    };

    this.listener = new Listener(this, 'Listener', {
      listenerName: names.prefix,
      protocol: ListenerProtocol.wireGuard({
        decapsulatedDelivery: true,
        peers: config.sites.map((site) => ({ publicKey: site.publicKey })),
      }),
      clientRestrictions: { networks: config.network.clientCidrs },
      metricsEnabled: true,
      logGroupName: this.errorsGroup.logGroupName,
      role: listenerRole,
      destinations: [
        new FirehoseDestination({
          ...common,
          destinationName: names.destination('archive'),
          description: 'Original message bytes (base64) to the Object Lock archive through Firehose.',
          deliveryStream: props.archiveStream,
          formatter: Formatter.BASE64,
          batching: { count: 500, timeoutInSeconds: 10, sizeInMB: 0.5 },
        }),
        new CloudWatchLogsDestination({
          ...common,
          destinationName: names.destination('logs'),
          description: 'Parsed messages (syslog formatter) as JSON events in CloudWatch Logs.',
          logGroup: this.logsGroup,
          formatter: Formatter.SYSLOG,
          filterExpression: config.ingest.logsFilterExpression || undefined,
          batching: { count: 200, timeoutInSeconds: 5, sizeInMB: 0.5 },
        }),
        new FirehoseDestination({
          ...common,
          destinationName: names.destination('analytics'),
          description: 'Parsed messages (syslog formatter) to Firehose for Parquet conversion.',
          deliveryStream: props.analyticsStream,
          formatter: Formatter.SYSLOG,
          batching: { count: 500, timeoutInSeconds: 10, sizeInMB: 0.5 },
        }),
      ],
    });

    // Syslog content metrics from the JSON events. Unparsed messages have null severity, so they
    // don't match the severity filters.
    const namespace = names.metricNamespace;
    const filter = (id: string, pattern: string, metricName: string, dimensions?: Record<string, string>) =>
      new logs.MetricFilter(this, id, {
        logGroup: this.logsGroup,
        filterName: metricName,
        filterPattern: logs.FilterPattern.literal(pattern),
        metricNamespace: namespace,
        metricName,
        metricValue: '1',
        unit: cloudwatch.Unit.COUNT,
        // CloudWatch doesn't allow a default value on filters with dimensions.
        ...(dimensions ? { dimensions } : { defaultValue: 0 }),
      });
    filter('MessageCountFilter', '{ $.Formatter = "syslog" }', 'MessageCount');
    filter('HighSeverityFilter', '{ $.Data.severity <= 2 }', 'HighSeverityCount');
    filter('SeverityFilter', '{ $.Data.severity >= 0 }', 'SeverityCount', { Severity: '$.Data.severity_name' });
    filter('ParseFailureFilter', '{ $.Data.parse_ok IS FALSE }', 'ParseFailureCount');
    filter('SiteMessageFilter', '{ $.Formatter = "syslog" }', 'SiteMessageCount', { PeerKey: '$.Remote.PeerKey' });

    const metric = (metricName: string, dimensionsMap?: Record<string, string>) =>
      new cloudwatch.Metric({ namespace, metricName, dimensionsMap, statistic: 'Sum' });
    this.metrics = {
      messages: metric('MessageCount'),
      highSeverity: metric('HighSeverityCount'),
      parseFailures: metric('ParseFailureCount'),
      severity: (name) => metric('SeverityCount', { Severity: name }),
      site: (publicKey) => metric('SiteMessageCount', { PeerKey: publicKey }),
    };

    // Contributor Insights: busiest sites, devices, and host/app pairs.
    const rule = (id: string, suffix: string, keys: string[]) => new cloudwatch.CfnInsightRule(this, id, {
      ruleName: `${names.prefix}-${suffix}`,
      ruleState: 'ENABLED',
      ruleBody: Stack.of(this).toJsonString({
        Schema: { Name: 'CloudWatchLogRule', Version: 1 },
        LogGroupNames: [this.logsGroup.logGroupName],
        LogFormat: 'JSON',
        Contribution: { Keys: keys, Filters: [] },
        AggregateOn: 'Count',
      }),
    });
    rule('TopSitesRule', 'top-sites', ['$.Remote.PeerKey']);
    rule('TopDevicesRule', 'top-devices', ['$.Remote.PeerKey', '$.Inner.SourceAddress']);
    rule('TopHostsAppsRule', 'top-hosts-apps', ['$.Data.hostname', '$.Data.app_name']);
  }

  /** A Proxylity metric for the listener or one of the destinations. */
  static proxylityMetric(metricName: string, dimension: 'Listener' | 'Destination', name: string): cloudwatch.Metric {
    return new cloudwatch.Metric({
      namespace: PROXYLITY_METRICS_NAMESPACE,
      metricName,
      dimensionsMap: { [dimension]: name },
      statistic: 'Sum',
    });
  }
}
