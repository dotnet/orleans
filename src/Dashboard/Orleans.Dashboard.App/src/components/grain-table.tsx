import React from 'react';

interface GrainStat {
  grainType: string;
  siloAddress: string;
  activationCount: number;
  totalSeconds: number;
  totalAwaitTime: number;
  totalCalls: number;
  totalExceptions: number;
}

interface AggregatedGrainStat {
  grainType: string;
  activationCount: number;
  totalSeconds: number;
  totalAwaitTime: number;
  totalCalls: number;
  totalExceptions: number;
}

interface GrainTableProps {
  data: GrainStat[];
  silo?: string;
}

interface GrainTableState {
  sortBy: string;
  sortByAsc: boolean;
}

export default class GrainTable extends React.Component<GrainTableProps, GrainTableState> {
  constructor(props: GrainTableProps) {
    super(props);
    this.state = {
      sortBy: 'activationCount',
      sortByAsc: false
    };
    this.handleChangeSort = this.handleChangeSort.bind(this);
  }

  getSorter(): ((a: AggregatedGrainStat, b: AggregatedGrainStat) => number) | null {
    let sorter: (a: AggregatedGrainStat, b: AggregatedGrainStat) => number;
    switch (this.state.sortBy) {
      case 'activationCount':
        sorter = this.state.sortByAsc
          ? sortByActivationCountAsc
          : sortByActivationCountDesc;
        break;
      case 'grain':
        sorter = this.state.sortByAsc ? sortByGrainAsc : sortBygrainDesc;
        break;
      case 'exceptionRate':
        sorter = this.state.sortByAsc
          ? sortByExceptionRateAsc
          : sortByExceptionRateDesc;
        break;
      case 'totalCalls':
        sorter = this.state.sortByAsc
          ? sortBytotalCallsAsc
          : sortBytotalCallsDec;
        break;
      case 'totalAwaitTime':
        sorter = this.state.sortByAsc
          ? sortByTotalAwaitTimeAsc
          : sortByTotalAwaitTimeDesc;
        break;
      default:
        sorter = () => 0;
        break;
    }
    return sorter;
  }

  handleChangeSort(e: React.MouseEvent<HTMLTableHeaderCellElement>) {
    const column = e.currentTarget.dataset['column'];
    if (column) {
      this.setState({
        sortBy: column,
        sortByAsc: this.state.sortBy === column ? !this.state.sortByAsc : false
      });
    }
  }

  renderStat = (stat: AggregatedGrainStat) => {
    const parts = stat.grainType.split('.');
    const grainClassName = parts[parts.length - 1];
    const systemGrain = stat.grainType.startsWith('Orleans.');
    const dashboardGrain = stat.grainType.startsWith('OrleansDashboard.');
    return (
      <tr key={stat.grainType}>
        <td style={{ textOverflow: 'ellipsis' }} title={stat.grainType}>
          <a href={`#/grain/${stat.grainType}`}>{grainClassName}</a>
        </td>
        <td>
          {systemGrain ? (
            <span className="label label-primary">System Grain</span>
          ) : null}
          {dashboardGrain ? (
            <span className="label label-primary">Dashboard Grain</span>
          ) : null}
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
            <strong>{(stat.totalCalls / 100).toFixed(2)}</strong>{' '}
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
  };

  render() {
    const grainTypes: { [key: string]: AggregatedGrainStat } = {};
    if (!this.props.data) return null;

    this.props.data.forEach(stat => {
      if (this.props.silo && stat.siloAddress !== this.props.silo) return;

      if (!grainTypes[stat.grainType]) {
        grainTypes[stat.grainType] = {
          grainType: stat.grainType,
          activationCount: 0,
          totalSeconds: 0,
          totalAwaitTime: 0,
          totalCalls: 0,
          totalExceptions: 0
        };
      }

      const x = grainTypes[stat.grainType];
      x.activationCount += stat.activationCount;
      x.totalSeconds += stat.totalSeconds;
      x.totalAwaitTime += stat.totalAwaitTime;
      x.totalCalls += stat.totalCalls;
      x.totalExceptions += stat.totalExceptions;
    });

    const values = Object.keys(grainTypes).map(key => {
      return grainTypes[key];
    });

    const sorter = this.getSorter();
    if (sorter) {
      values.sort(sorter);
    }

    return (
      <table className="table">
        <tbody>
          <tr>
            <th data-column="grain" onClick={this.handleChangeSort}>
              Grain{' '}
              {this.state.sortBy === 'grain' ? (
                this.state.sortByAsc ? (
                  <i className="fa fa-arrow-up" />
                ) : (
                  <i className="fa fa-arrow-down" />
                )
              ) : null}
            </th>
            <th />
            <th
              data-column="activationCount"
              onClick={this.handleChangeSort}
              style={{ textAlign: 'right' }}
            >
              Activations{' '}
              {this.state.sortBy === 'activationCount' ? (
                this.state.sortByAsc ? (
                  <i className="fa fa-arrow-up" />
                ) : (
                  <i className="fa fa-arrow-down" />
                )
              ) : null}
            </th>
            <th
              data-column="exceptionRate"
              onClick={this.handleChangeSort}
              style={{ textAlign: 'right' }}
            >
              Exception rate{' '}
              {this.state.sortBy === 'exceptionRate' ? (
                this.state.sortByAsc ? (
                  <i className="fa fa-arrow-up" />
                ) : (
                  <i className="fa fa-arrow-down" />
                )
              ) : null}
            </th>
            <th
              data-column="totalCalls"
              onClick={this.handleChangeSort}
              style={{ textAlign: 'right' }}
            >
              Throughput{' '}
              {this.state.sortBy === 'totalCalls' ? (
                this.state.sortByAsc ? (
                  <i className="fa fa-arrow-up" />
                ) : (
                  <i className="fa fa-arrow-down" />
                )
              ) : null}
            </th>
            <th
              data-column="totalAwaitTime"
              onClick={this.handleChangeSort}
              style={{ textAlign: 'right' }}
            >
              Latency{' '}
              {this.state.sortBy === 'totalAwaitTime' ? (
                this.state.sortByAsc ? (
                  <i className="fa fa-arrow-up" />
                ) : (
                  <i className="fa fa-arrow-down" />
                )
              ) : null}
            </th>
          </tr>
          {values.map(this.renderStat)}
        </tbody>
      </table>
    );
  }
}

function sortByActivationCountAsc(a: AggregatedGrainStat, b: AggregatedGrainStat): number {
  return a.activationCount - b.activationCount;
}

function sortByActivationCountDesc(a: AggregatedGrainStat, b: AggregatedGrainStat): number {
  return sortByActivationCountAsc(b, a);
}

function sortByGrainAsc(a: AggregatedGrainStat, b: AggregatedGrainStat): number {
  const parts = (x: AggregatedGrainStat) => x.grainType.split('.');
  const grainClassName = (x: AggregatedGrainStat) => parts(x)[parts(x).length - 1];
  return grainClassName(a) < grainClassName(b)
    ? -1
    : grainClassName(a) > grainClassName(b)
    ? 1
    : 0;
}

function sortBygrainDesc(a: AggregatedGrainStat, b: AggregatedGrainStat): number {
  return sortByGrainAsc(b, a);
}

function sortByExceptionRateAsc(a: AggregatedGrainStat, b: AggregatedGrainStat): number {
  return a.totalExceptions - b.totalExceptions;
}

function sortByExceptionRateDesc(a: AggregatedGrainStat, b: AggregatedGrainStat): number {
  return sortByExceptionRateAsc(b, a);
}

function sortBytotalCallsAsc(a: AggregatedGrainStat, b: AggregatedGrainStat): number {
  return a.totalCalls - b.totalCalls;
}

function sortBytotalCallsDec(a: AggregatedGrainStat, b: AggregatedGrainStat): number {
  return sortBytotalCallsAsc(b, a);
}

function sortByTotalAwaitTimeAsc(a: AggregatedGrainStat, b: AggregatedGrainStat): number {
  if (a.totalCalls === 0 && b.totalCalls === 0) {
    return 0;
  } else if (a.totalCalls === 0 || b.totalCalls === 0) {
    return a.totalAwaitTime - b.totalAwaitTime;
  } else {
    return a.totalAwaitTime / a.totalCalls - b.totalAwaitTime / b.totalCalls;
  }
}

function sortByTotalAwaitTimeDesc(a: AggregatedGrainStat, b: AggregatedGrainStat): number {
  return sortByTotalAwaitTimeAsc(b, a);
}
