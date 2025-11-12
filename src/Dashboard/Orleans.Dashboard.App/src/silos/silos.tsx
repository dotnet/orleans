import React from 'react';
import CounterWidget from '../components/counter-widget';
import ChartWidget from '../components/multi-series-chart-widget';
import HostsWidget from './host-table';
import SiloGrid from './silo-grid';
import Panel from '../components/panel';

interface DashboardCounters {
  totalActiveHostCount: number;
  totalActiveHostCountHistory: number[];
  [key: string]: any;
}

interface SilosProps {
  dashboardCounters: DashboardCounters;
}

export default class Silos extends React.Component<SilosProps> {
  render() {
    return (
      <div>
        <div className="row">
          <div className="col-md-4">
            <CounterWidget
              icon="database"
              counter={this.props.dashboardCounters.totalActiveHostCount}
              title="Active Silos"
              style={{ height: '120px' }}
            />
          </div>
          <div className="col-md-8">
            <div className="info-box" style={{ padding: '5px', height: '120px', display: 'flex', flexDirection: 'column' }}>
              <ChartWidget
                series={[
                  this.props.dashboardCounters.totalActiveHostCountHistory
                ]}
              />
            </div>
          </div>
        </div>

        <Panel title="Silo Health">
          <HostsWidget dashboardCounters={this.props.dashboardCounters} />
        </Panel>
        <Panel title="Silo Map">
          <SiloGrid dashboardCounters={this.props.dashboardCounters} />
        </Panel>
      </div>
    );
  }
}
