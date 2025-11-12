import React from 'react';
import CounterWidget from '../components/counter-widget';
import ChartWidget from '../components/multi-series-chart-widget';
import GrainBreakdown from '../components/grain-table';
import Panel from '../components/panel';

interface SimpleGrainStat {
  activationCount: number;
  [key: string]: any;
}

interface DashboardCounters {
  simpleGrainStats: SimpleGrainStat[];
  totalActivationCountHistory: number[];
}

interface GrainsProps {
  dashboardCounters: DashboardCounters;
}

export default class Grains extends React.Component<GrainsProps> {
  render() {
    const stats = { totalActivationCount: 0 };
    this.props.dashboardCounters.simpleGrainStats.forEach(stat => {
      stats.totalActivationCount += stat.activationCount;
    });

    return (
      <div>
        <div className="row">
          <div className="col-md-4">
            <CounterWidget
              icon="cubes"
              counter={stats.totalActivationCount}
              title="Total Activations"
              style={{ height: '120px' }}
            />
          </div>
          <div className="col-md-8">
            <div className="info-box" style={{ padding: '5px', height: '120px', display: 'flex', flexDirection: 'column' }}>
              <ChartWidget
                series={[
                  this.props.dashboardCounters.totalActivationCountHistory
                ]}
              />
            </div>
          </div>
        </div>
        <Panel title="Activations by Type">
          <GrainBreakdown
            data={this.props.dashboardCounters.simpleGrainStats}
          />
        </Panel>
      </div>
    );
  }
}
