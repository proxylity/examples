import { Stack } from 'aws-cdk-lib';

/**
 * Every physical name in the example, derived from the stack name and region so a second stack in
 * the same region, or the same stack name in another region, never collides. Listener and
 * destination names must be unique within the account.
 */
export class Names {
  /** `<stack>-<region>`: listener, workgroup, dashboard and alarm name prefix. */
  readonly prefix: string;
  readonly archiveBucket: string;
  readonly analyticsBucket: string;
  readonly athenaResultsBucket: string;
  readonly siemBackupBucket: string;
  /** Glue database names can't contain hyphens. */
  readonly glueDatabase: string;
  readonly logGroupPrefix: string;
  readonly metricNamespace: string;

  constructor(stack: Stack) {
    this.prefix = `${stack.stackName}-${stack.region}`;
    this.archiveBucket = `${this.prefix}-archive-${stack.account}`;
    this.analyticsBucket = `${this.prefix}-analytics-${stack.account}`;
    this.athenaResultsBucket = `${this.prefix}-athena-${stack.account}`;
    this.siemBackupBucket = `${this.prefix}-siem-${stack.account}`;
    this.glueDatabase = this.prefix.replace(/-/g, '_');
    this.logGroupPrefix = `/proxylity-examples/syslog/${this.prefix}`;
    this.metricNamespace = `Syslog/${this.prefix}`;
  }

  stream(kind: 'archive' | 'analytics' | 'siem'): string {
    return `${this.prefix}-${kind}`;
  }

  destination(kind: 'archive' | 'logs' | 'analytics'): string {
    return `${this.prefix}-${kind}`;
  }
}
