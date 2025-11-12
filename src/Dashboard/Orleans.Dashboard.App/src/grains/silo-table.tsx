import React from 'react';

interface SiloStat {
  siloAddress: string;
  activationCount: number;
  totalSeconds: number;
  totalAwaitTime: number;
  totalCalls: number;
  totalExceptions: number;
}

interface GrainStat {
  siloAddress: string;
  grainType: string;
  activationCount: number;
  totalSeconds: number;
  totalAwaitTime: number;
  totalCalls: number;
  totalExceptions: number;
}

interface SiloTableProps {
  data: GrainStat[];
  grainType?: string;
}

export default class SiloTable extends React.Component<SiloTableProps> {
  renderStat(stat: SiloStat) {
    return (
      <tr key={stat.siloAddress}>
        <td style={{ textOverflow: 'ellipsis' }} title={stat.siloAddress}>
          <a href={`#/host/${stat.siloAddress}`}>{stat.siloAddress}</a>
        </td>
        <td>
          <span className="pull-right">
            <strong>{stat.activationCount}</strong>
          </span>
        </td>
        <td>
          <span className="pull-right">
            <strong>
              {stat.totalCalls === 0
                ? '0.00'
                : ((100 * stat.totalExceptions) / stat.totalCalls).toFixed(2)}
            </strong>{' '}
            <small>%</small>
          </span>
        </td>
        <td>
          <span className="pull-right">
            <strong>
              {stat.totalSeconds === 0
                ? '0'
                : (stat.totalCalls / 100).toFixed(2)}
            </strong>{' '}
            <small>req/sec</small>
          </span>
        </td>
        <td>
          <span className="pull-right">
            <strong>
              {stat.totalCalls === 0
                ? '0'
                : (stat.totalAwaitTime / stat.totalCalls).toFixed(2)}
            </strong>{' '}
            <small>ms/req</small>
          </span>
        </td>
      </tr>
    );
  }

  render() {
    const silos: { [key: string]: SiloStat } = {};
    if (!this.props.data) return null;

    this.props.data.forEach(stat => {
      if (!silos[stat.siloAddress]) {
        silos[stat.siloAddress] = {
          siloAddress: stat.siloAddress,
          activationCount: 0,
          totalSeconds: 0,
          totalAwaitTime: 0,
          totalCalls: 0,
          totalExceptions: 0
        };
      }

      if (this.props.grainType && stat.grainType !== this.props.grainType)
        return;

      const x = silos[stat.siloAddress];
      x.activationCount += stat.activationCount;
      x.totalSeconds += stat.totalSeconds;
      x.totalAwaitTime += stat.totalAwaitTime;
      x.totalCalls += stat.totalCalls;
      x.totalExceptions += stat.totalExceptions;
    });

    const values = Object.keys(silos)
      .map(function(key) {
        const x = silos[key];
        x.siloAddress = key;
        return x;
      })
      .sort(function(a, b) {
        return b.activationCount - a.activationCount;
      });

    return (
      <table className="table">
        <tbody>
          <tr>
            <th style={{ textAlign: 'left' }}>Silo</th>
            <th style={{ textAlign: 'right' }}>Activations</th>
            <th style={{ textAlign: 'right' }}>Exception rate</th>
            <th style={{ textAlign: 'right' }}>Throughput</th>
            <th style={{ textAlign: 'right' }}>Latency</th>
          </tr>
          {values.map(stat => this.renderStat(stat))}
        </tbody>
      </table>
    );
  }
}
