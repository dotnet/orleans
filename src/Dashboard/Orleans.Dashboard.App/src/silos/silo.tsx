import React from 'react';
import Gauge from '../components/gauge-widget';
import PropertiesWidget from '../components/properties-widget';
import GrainBreakdown from '../components/grain-table';
import ChartWidget from '../components/multi-series-chart-widget';
import Panel from '../components/panel';
import Chart from '../components/time-series-chart';

interface SiloDataPoint {
  count: number;
  elapsedTime: number;
  period: number | string;
  exceptionCount: number;
  cpuUsage?: number;
  totalPhysicalMemory?: number;
  availableMemory?: number;
  activationCount?: number;
  recentlyUsedActivationCount?: number;
  clientCount?: number;
  receivedMessages?: number;
  sentMessages?: number;
  receiveQueueLength?: number;
  requestQueueLength?: number;
  sendQueueLength?: number;
}

interface SiloStatsData {
  [key: string]: SiloDataPoint;
}

interface SimpleGrainStat {
  siloAddress: string;
  [key: string]: any;
}

interface Host {
  siloAddress: string;
  hostName?: string;
  roleName?: string;
  siloName?: string;
  proxyPort?: number;
  updateZone?: number;
  faultZone?: number;
}

interface DashboardCounters {
  simpleGrainStats?: SimpleGrainStat[];
  hosts: Host[];
}

interface SiloProperties {
  orleansVersion?: string;
  hostVersion?: string;
}

interface SiloProps {
  silo: string;
  data: (SiloDataPoint | null)[];
  siloProperties: SiloProperties;
  dashboardCounters: DashboardCounters;
  siloStats: SiloStatsData;
}

interface SiloGraphProps {
  stats: SiloStatsData;
}

const SiloGraph: React.FC<SiloGraphProps> = (props) => {
  const values: SiloDataPoint[] = [];
  const timepoints: (number | string)[] = [];
  Object.keys(props.stats).forEach(key => {
    values.push(props.stats[key]);
    timepoints.push(props.stats[key].period);
  });

  if (!values.length) {
    return null;
  }

  while (values.length < 100) {
    values.unshift({ count: 0, elapsedTime: 0, period: 0, exceptionCount: 0 });
    timepoints.unshift('');
  }

  return (
    <div>
      <Chart
        timepoints={timepoints}
        series={[
          values.map(z => z.exceptionCount),
          values.map(z => z.count),
          values.map(z => (z.count === 0 ? 0 : z.elapsedTime / z.count))
        ]}
      />
    </div>
  );
};

export default class Silo extends React.Component<SiloProps> {
  hasData(value: (SiloDataPoint | null)[]): boolean {
    for (let i = 0; i < value.length; i++) {
      if (value[i] !== null) return true;
    }
    return false;
  }

  querySeries(lambda: (x: SiloDataPoint) => number): number[] {
    return this.props.data.map(function(x) {
      if (!x) return 0;
      return lambda(x);
    });
  }

  hasSeries(lambda: (x: SiloDataPoint) => boolean): boolean {
    let hasValue = false;

    for (const key in this.props.data) {
      const value = this.props.data[key];
      if (value && lambda(value)) {
        hasValue = true;
      }
    }

    return hasValue;
  }

  render() {
    if (!this.hasData(this.props.data)) {
      return (
        <Panel title="Error">
          <div>
            <p className="lead">No data available for this silo</p>
            <p>
              <a href="#/silos">Show all silos</a>
            </p>
          </div>
        </Panel>
      );
    }

    const last = this.props.data[this.props.data.length - 1]!;
    const properties: { [key: string]: string | number } = {
      Clients: last.clientCount || '0',
      'Messages received': last.receivedMessages || '0',
      'Messages sent': last.sentMessages || '0',
      'Receive queue': last.receiveQueueLength || '0',
      'Request queue': last.requestQueueLength || '0',
      'Send queue': last.sendQueueLength || '0'
    };

    const grainStats = (
      this.props.dashboardCounters.simpleGrainStats || []
    ).filter(function(x) {
      return x.siloAddress === this.props.silo;
    }, this);

    const silo =
      this.props.dashboardCounters.hosts.filter(
        x => x.siloAddress === this.props.silo
      )[0] || {};

    const configuration: { [key: string]: string | number | undefined } = {
      'Host name': silo.hostName,
      'Role name': silo.roleName,
      'Silo name': silo.siloName,
      'Proxy port': silo.proxyPort,
      'Update zone': silo.updateZone,
      'Fault zone': silo.faultZone
    };

    if (this.props.siloProperties.orleansVersion) {
      configuration[
        'Orleans version'
      ] = this.props.siloProperties.orleansVersion;
    }

    if (this.props.siloProperties.hostVersion) {
      configuration['Host version'] = this.props.siloProperties.hostVersion;
    }

    let cpuGauge: React.ReactNode;
    let memGauge: React.ReactNode;

    if (this.hasSeries(x => (x.cpuUsage || 0) > 0)) {
      cpuGauge = (
        <div>
          <Gauge
            value={last.cpuUsage || 0}
            max={100}
            title="CPU Usage"
            description={Math.floor(last.cpuUsage || 0) + '% utilisation'}
          />
          <div style={{ width: '100%', height: '80px' }}>
            <ChartWidget series={[this.querySeries(x => x.cpuUsage || 0)]} />
          </div>
        </div>
      );
    } else {
      cpuGauge = (
        <div style={{ textAlign: 'center', position: 'relative', minHeight: '100px' }}>
          <h4>CPU Usage</h4>
          <div style={{ height: '200px', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <div style={{ lineHeight: '40px' }}>No data available</div>
          </div>
          <div style={{ height: '80px' }}></div>
        </div>
      );
    }

    if (this.hasSeries(x => (x.totalPhysicalMemory || 0) - (x.availableMemory || 0) > 0)) {
      memGauge = (
        <div>
          <Gauge
            value={
              (last.totalPhysicalMemory || 0) - (last.availableMemory || 0)
            }
            max={last.totalPhysicalMemory || 1}
            title="Memory Usage"
            description={
              Math.floor((last.availableMemory || 0) / (1024 * 1024)) +
              ' MB free'
            }
          />
          <div style={{ width: '100%', height: '80px' }}>
            <ChartWidget
              series={[
                this.querySeries(
                  x => ((x.totalPhysicalMemory || 0) - (x.availableMemory || 0)) / (1024 * 1024)
                )
              ]}
            />
          </div>
        </div>
      );
    } else {
      memGauge = (
        <div style={{ textAlign: 'center', position: 'relative', minHeight: '100px' }}>
          <h4>Memory Usage</h4>
          <div style={{ height: '200px', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <div style={{ lineHeight: '40px' }}>No data available</div>
          </div>
          <div style={{ height: '80px' }}></div>
        </div>
      );
    }

    return (
      <div>
        <Panel title="Overview">
          <div className="row">
            <div className="col-md-4">{cpuGauge}</div>
            <div className="col-md-4">{memGauge}</div>
            <div className="col-md-4">
              <Gauge
                value={last.recentlyUsedActivationCount || 0}
                max={last.activationCount || 1}
                title="Grain Usage"
                description={
                  (last.activationCount || 0) +
                  ' activations, ' +
                  Math.floor(
                    ((last.recentlyUsedActivationCount || 0) * 100) /
                      (last.activationCount || 1)
                  ) +
                  '% recently used'
                }
              />
              <div style={{ width: '100%', height: '80px' }}>
                <ChartWidget
                  series={[
                    this.querySeries(x => x.activationCount || 0),
                    this.querySeries(x => x.recentlyUsedActivationCount || 0)
                  ]}
                />
              </div>
            </div>
          </div>
        </Panel>

        <Panel title="Silo Profiling">
          <div>
            <span>
              <strong style={{ color: '#783988', fontSize: '25px' }}>/</strong>{' '}
              number of requests per second
              <br />
              <strong style={{ color: '#EC1F1F', fontSize: '25px' }}>
                /
              </strong>{' '}
              failed requests
            </span>
            <span className="pull-right">
              <strong style={{ color: '#EC971F', fontSize: '25px' }}>/</strong>{' '}
              average latency in milliseconds
            </span>
            <SiloGraph stats={this.props.siloStats} />
          </div>
        </Panel>

        <div className="row">
          <div className="col-md-6">
            <Panel title="Silo Counters">
              <div>
                <PropertiesWidget data={properties} />
                <a href={`#/host/${this.props.silo}/counters`}>
                  View all counters
                </a>
              </div>
            </Panel>
          </div>
          <div className="col-md-6">
            <Panel title="Silo Properties">
              <PropertiesWidget data={configuration} />
            </Panel>
          </div>
        </div>

        <Panel title="Activations by Type">
          <GrainBreakdown data={grainStats} silo={this.props.silo} />
        </Panel>
      </div>
    );
  }
}
/*

dateTime: "2015-12-30T17:02:32.6695724Z"

cpuUsage: 11.8330326
activationCount: 4
availableMemory: 4301320000
totalPhysicalMemory: 8589934592
memoryUsage: 8618116
recentlyUsedActivationCount: 2


clientCount: 0
isOverloaded: false

receiveQueueLength: 0
requestQueueLength: 0
sendQueueLength: 0

receivedMessages: 0
sentMessages: 0

*/
