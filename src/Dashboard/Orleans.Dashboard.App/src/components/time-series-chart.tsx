import React from 'react';
import { Line as Chart } from 'react-chartjs-2';

interface TimeSeriesChartProps {
  timepoints: string[];
  series: number[][];
}

interface TimeSeriesChartState {
  width: number;
}

// this control is a bit of a temporary hack, until I have a multi-series chart widget
export default class TimeSeriesChart extends React.Component<TimeSeriesChartProps, TimeSeriesChartState> {
  private containerRef: HTMLDivElement | null;
  private options: any;

  constructor(props: TimeSeriesChartProps) {
    super(props);
    this.state = {
      width: 0
    };
    this.containerRef = null;
    this.getWidth = this.getWidth.bind(this);
    this.setContainerRef = this.setContainerRef.bind(this);

    this.options = {
      plugins: {
        legend: { display: false },
        tooltip: { enabled: false }
      },
      maintainAspectRatio: false,
      animation: false,
      responsive: true,
      interaction: {
        mode: 'index',
        intersect: false
      },
      scales: {
        x: {
          display: true,
          grid: {
            offset: false,
            drawOnChartArea: false
          },
          ticks: {
            autoSkip: false,
            maxRotation: 0,
            minRotation: 0,
            font: { size: 9 }
          }
        },
        y1: {
          type: 'linear',
          display: true,
          position: 'left',
          grid: { drawOnChartArea: false },
          ticks: { beginAtZero: true }
        },
        y2: {
          type: 'linear',
          display: true,
          position: 'right',
          grid: { drawOnChartArea: false },
          ticks: { beginAtZero: true }
        }
      }
    };
  }

  setContainerRef(element: HTMLDivElement | null) {
    this.containerRef = element;
  }

  componentDidMount() {
    this.getWidth();
  }

  componentDidUpdate(prevProps: TimeSeriesChartProps, prevState: TimeSeriesChartState) {
    if (prevState.width === 0 && this.state.width === 0) {
      this.getWidth();
    }
  }

  getWidth() {
    if (!this.containerRef) {
      return;
    }

    this.setState({ width: this.containerRef.offsetWidth });
  }

  renderChart() {
    if (this.state.width === 0) {
      return null;
    }

    const data = {
      labels: this.props.timepoints.map(timepoint => {
        if (timepoint) {
          try {
            if (new Date(timepoint).getSeconds() % 30 == 0) {
              return new Date(timepoint).toLocaleTimeString();
            }
          } catch (e) {
            // not a valid date string
          }
        }

        return '';
      }),
      datasets: [
        {
          label: 'Average Latency',
          backgroundColor: `rgba(236,151,31,0.2)`,
          borderColor: `rgba(236,151,31,1)`,
          data: this.props.series[2],
          pointRadius: 0,
          yAxisID: 'y2',
          fill: true,
          tension: 0.4,
          borderWidth: 2
        },
        {
          label: 'Failed Requests',
          backgroundColor: `rgba(236,31,31,0.4)`,
          borderColor: `rgba(236,31,31,1)`,
          data: this.props.series[0],
          pointRadius: 0,
          yAxisID: 'y1',
          fill: true,
          tension: 0.4,
          borderWidth: 2
        },
        {
          label: 'Requests per Second',
          backgroundColor: `rgba(120,57,136,0.4)`,
          borderColor: `rgba(120,57,136,1)`,
          data: this.props.series[1],
          pointRadius: 0,
          yAxisID: 'y1',
          fill: true,
          tension: 0.4,
          borderWidth: 2
        }
      ]
    };

    return (
      <Chart
        data={data}
        options={this.options}
        width={this.state.width}
        height={180}
      />
    );
  }

  render() {
    return <div ref={this.setContainerRef}>{this.renderChart()}</div>;
  }
}
