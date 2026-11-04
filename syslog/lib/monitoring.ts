import { Duration, Stack } from 'aws-cdk-lib';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as actions from 'aws-cdk-lib/aws-cloudwatch-actions';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as firehose from 'aws-cdk-lib/aws-kinesisfirehose';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as subscriptions from 'aws-cdk-lib/aws-sns-subscriptions';
import { Construct } from 'constructs';
import { SyslogConfig } from './config';
import { Ingest } from './ingest';
import { Names } from './names';

const SEVERITIES = ['emerg', 'alert', 'crit', 'err', 'warning', 'notice', 'info', 'debug'];
/** Above this many sites the per-site graph switches from labeled series to a SEARCH expression. */
const MAX_LABELED_SITES = 50;
const FIVE_MINUTES = Duration.minutes(5);

/** The alarm topic, pipeline and per-site alarms, and the dashboard. */
export class Monitoring extends Construct {
  readonly topic: sns.Topic;
  readonly dashboard: cloudwatch.Dashboard;

  constructor(scope: Construct, id: string, props: {
    config: SyslogConfig;
    names: Names;
    ingest: Ingest;
    archiveStream: firehose.CfnDeliveryStream;
    analyticsStream: firehose.CfnDeliveryStream;
  }) {
    super(scope, id);
    const { config, names, ingest } = props;
    const { partition, region, account } = Stack.of(this);
    const listenerName = names.prefix;
    const destinations = (['archive', 'logs', 'analytics'] as const).map((kind) => ({ kind, name: names.destination(kind) }));
    const streams = [
      { kind: 'archive', name: names.stream('archive') },
      { kind: 'analytics', name: names.stream('analytics') },
    ];
    const firehoseMetric = (metricName: string, stream: string, statistic = 'Sum') =>
      new cloudwatch.Metric({ namespace: 'AWS/Firehose', metricName, dimensionsMap: { DeliveryStreamName: stream }, statistic, period: FIVE_MINUTES });

    this.topic = new sns.Topic(this, 'Topic', { topicName: `${names.prefix}-syslog-alarms`, enforceSSL: true });
    this.topic.addToResourcePolicy(new iam.PolicyStatement({
      sid: 'CloudWatchAlarmsFromThisAccount',
      principals: [new iam.ServicePrincipal('cloudwatch.amazonaws.com')],
      actions: ['sns:Publish'],
      resources: [this.topic.topicArn],
      conditions: {
        StringEquals: { 'aws:SourceAccount': account },
        ArnLike: { 'aws:SourceArn': `arn:${partition}:cloudwatch:${region}:${account}:alarm:${names.prefix}-*` },
      },
    }));
    if (config.alarms.email) this.topic.addSubscription(new subscriptions.EmailSubscription(config.alarms.email));

    const alarm = (id: string, suffix: string, description: string, metric: cloudwatch.IMetric, opts: {
      threshold: number;
      comparison: cloudwatch.ComparisonOperator;
      missing: cloudwatch.TreatMissingData;
      periods?: number;
    }) => {
      const a = new cloudwatch.Alarm(this, id, {
        alarmName: `${names.prefix}-${suffix}`,
        alarmDescription: description,
        metric,
        threshold: opts.threshold,
        comparisonOperator: opts.comparison,
        evaluationPeriods: opts.periods ?? 1,
        treatMissingData: opts.missing,
      });
      a.addAlarmAction(new actions.SnsAction(this.topic));
      a.addOkAction(new actions.SnsAction(this.topic));
      return a;
    };
    const { GREATER_THAN_OR_EQUAL_TO_THRESHOLD: atLeast, GREATER_THAN_THRESHOLD: above, LESS_THAN_THRESHOLD: below } = cloudwatch.ComparisonOperator;
    const { BREACHING: breaching, NOT_BREACHING: notBreaching } = cloudwatch.TreatMissingData;

    const pipelineAlarms: cloudwatch.Alarm[] = [
      alarm('TunnelDown', 'tunnel-down',
        'No WireGuard protocol traffic (handshakes, keepalives) reached the listener for 10 minutes, so no site has a working tunnel. Requires PersistentKeepalive = 25 on every site.',
        Ingest.proxylityMetric('IngressPackets', 'Listener', listenerName).with({ period: FIVE_MINUTES }),
        { threshold: 1, comparison: below, missing: breaching, periods: 2 }),
      alarm('PipelineSilence', 'pipeline-silence',
        'No syslog messages from any site reached CloudWatch Logs in the window. Read it together with the tunnel-down and site-silence alarms.',
        ingest.metrics.messages.with({ period: Duration.seconds(config.alarms.pipelineSilenceSeconds) }),
        { threshold: 1, comparison: below, missing: breaching }),
      alarm('HighSeverity', 'high-severity',
        'Messages with severity crit, alert or emerg (2 or lower) arrived. See the dashboard\'s recent high severity table.',
        ingest.metrics.highSeverity.with({ period: FIVE_MINUTES }),
        { threshold: 1, comparison: atLeast, missing: notBreaching }),
      alarm('ParseFailures', 'parse-failures',
        "Messages arrived that the syslog formatter couldn't parse (parse_ok false). Run the parse failures by device query.",
        ingest.metrics.parseFailures.with({ period: FIVE_MINUTES }),
        { threshold: 1, comparison: atLeast, missing: notBreaching }),
      alarm('ListenerErrors', 'listener-errors', 'Proxylity reported listener errors. See the errors log group.',
        Ingest.proxylityMetric('Errors', 'Listener', listenerName).with({ period: FIVE_MINUTES }),
        { threshold: 0, comparison: above, missing: notBreaching }),
      ...destinations.map((d) => alarm(`${d.kind}DestinationErrors`, `${d.kind}-destination-errors`,
        `Proxylity reported delivery errors for the ${d.kind} destination. See the errors log group.`,
        Ingest.proxylityMetric('Errors', 'Destination', d.name).with({ period: FIVE_MINUTES }),
        { threshold: 0, comparison: above, missing: notBreaching })),
      ...streams.map((s) => alarm(`${s.kind}Freshness`, `${s.kind}-freshness`,
        `The ${s.kind} Firehose stream is falling behind delivering to S3. See its firehose-${s.kind} log group.`,
        firehoseMetric('DeliveryToS3.DataFreshness', s.name, 'Maximum'),
        { threshold: config.alarms.freshnessSeconds, comparison: above, missing: notBreaching })),
      alarm('FailedConversion', 'failed-conversion',
        "Firehose couldn't convert records to Parquet. Failed records are under errors/format-conversion-failed/ in the analytics bucket.",
        firehoseMetric('FailedConversion.Records', names.stream('analytics')),
        { threshold: 0, comparison: above, missing: notBreaching }),
    ];

    // One silence alarm per site, straight from syslog.peers.json.
    const siteAlarms = config.sites.map((site) => alarm(`SiteSilence-${site.name}`, `site-silence-${site.name}`,
      `No syslog messages from site ${site.name} (WireGuard key ${site.publicKey}) in the window. ` +
        'If tunnel-down is also in alarm, the problem is the listener or connectivity; otherwise this site\'s tunnel or devices.',
      ingest.metrics.site(site.publicKey).with({ period: Duration.seconds(config.alarms.siteSilenceSeconds) }),
      { threshold: 1, comparison: below, missing: breaching }));

    // ---- Dashboard -------------------------------------------------------------------------
    const graph = (title: string, left: cloudwatch.IMetric[], opts: Partial<cloudwatch.GraphWidgetProps> = {}) =>
      new cloudwatch.GraphWidget({ title, left, height: 6, width: 8, period: FIVE_MINUTES, ...opts });
    const sum = (m: cloudwatch.Metric, label: string) => m.with({ label, period: FIVE_MINUTES });
    const perDestination = (metricName: string) =>
      destinations.map((d) => sum(Ingest.proxylityMetric(metricName, 'Destination', d.name), d.kind));

    const siteSeries: cloudwatch.IMetric[] = config.sites.length <= MAX_LABELED_SITES
      ? config.sites.map((site) => sum(ingest.metrics.site(site.publicKey), site.name))
      : [new cloudwatch.MathExpression({
        expression: `SEARCH('{"${names.metricNamespace}",PeerKey} MetricName="SiteMessageCount"', 'Sum', 300)`,
        usingMetrics: {},
        label: '',
        period: FIVE_MINUTES,
      })];

    const logsGroupName = ingest.logsGroup.logGroupName;
    const logsQuery = (title: string, lines: string[], width = 12) => new cloudwatch.LogQueryWidget({
      title,
      logGroupNames: [logsGroupName],
      queryLines: lines,
      view: cloudwatch.LogQueryVisualizationType.TABLE,
      width,
      height: 6,
    });

    this.dashboard = new cloudwatch.Dashboard(this, 'Dashboard', {
      dashboardName: names.prefix,
      start: '-PT6H',
      periodOverride: cloudwatch.PeriodOverride.INHERIT,
      widgets: [
        [new cloudwatch.TextWidget({
          markdown: `## Syslog: ${names.prefix}\nListener \`${listenerName}\`, ${config.sites.length} site(s). ` +
            `Metrics in \`${names.metricNamespace}\` and \`Proxylity/UDP Gateway\`. Saved Athena queries are in the \`${names.prefix}\` workgroup.`,
          width: 24,
          height: 2,
        })],
        [new cloudwatch.AlarmStatusWidget({
          title: 'Pipeline alarms',
          alarms: pipelineAlarms,
          sortBy: cloudwatch.AlarmStatusWidgetSortBy.STATE_UPDATED_TIMESTAMP,
          width: 24,
          height: 4,
        })],
        [new cloudwatch.AlarmStatusWidget({
          title: 'Site alarms',
          alarms: siteAlarms,
          sortBy: cloudwatch.AlarmStatusWidgetSortBy.STATE_UPDATED_TIMESTAMP,
          width: 24,
          height: Math.min(8, 2 + Math.ceil(siteAlarms.length / 4)),
        })],
        [
          graph('Syslog messages', [sum(ingest.metrics.messages, 'messages'), sum(ingest.metrics.highSeverity, 'crit or worse')]),
          graph('Messages by severity', SEVERITIES.map((s) => sum(ingest.metrics.severity(s), s)), { stacked: true }),
          graph('Parse failures', [sum(ingest.metrics.parseFailures, 'parse_ok false')]),
        ],
        [graph('Messages by site', siteSeries, { width: 24 })],
        [
          graph('Listener: handshakes and keepalives (size-weighted)', [
            sum(Ingest.proxylityMetric('IngressPackets', 'Listener', listenerName), 'ingress'),
            sum(Ingest.proxylityMetric('EgressPackets', 'Listener', listenerName), 'egress'),
          ], { width: 12 }),
          graph('Errors (listener and destinations)', [
            sum(Ingest.proxylityMetric('Errors', 'Listener', listenerName), 'listener'),
            ...perDestination('Errors'),
          ], { width: 12 }),
        ],
        [
          graph('Destination ingress bytes', perDestination('IngressBytes')),
          graph('Destination ingress packets (size-weighted)', perDestination('IngressPackets')),
          graph('Destination filtered packets (size-weighted)', perDestination('FilteredPackets')),
        ],
        [
          graph('Firehose records', streams.flatMap((s) => [
            firehoseMetric('IncomingRecords', s.name).with({ label: `${s.kind} in` }),
            firehoseMetric('DeliveryToS3.Records', s.name).with({ label: `${s.kind} to S3` }),
          ])),
          graph('Firehose delivery success and freshness',
            streams.map((s) => firehoseMetric('DeliveryToS3.Success', s.name, 'Average').with({ label: `${s.kind} success` })),
            { right: streams.map((s) => firehoseMetric('DeliveryToS3.DataFreshness', s.name, 'Maximum').with({ label: `${s.kind} freshness (s)` })) }),
          graph('Parquet conversion', [
            firehoseMetric('SucceedConversion.Records', names.stream('analytics')).with({ label: 'converted' }),
            firehoseMetric('FailedConversion.Records', names.stream('analytics')).with({ label: 'failed' }),
          ]),
        ],
        [logsQuery('Recent high severity', [
          'fields ReceivedAt, Remote.PeerKey, Inner.SourceAddress, Data.hostname, Data.app_name, Data.severity_name, Data.message',
          'filter Data.severity <= 2',
          'sort ReceivedAt desc',
          'limit 50',
        ], 24)],
        [
          logsQuery('Messages by site and device', [
            'stats count(*) as messages, latest(ReceivedAt) as last_seen by Remote.PeerKey, Inner.SourceAddress',
            'sort messages desc',
            'limit 50',
          ]),
          logsQuery('Messages by hostname', [
            'stats count(*) as messages, count_distinct(Inner.SourceAddress) as devices by Data.hostname, Data.app_name',
            'sort messages desc',
            'limit 50',
          ]),
        ],
      ],
    });
  }
}
