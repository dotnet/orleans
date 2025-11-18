import React from 'react';
import { Line as Chart } from 'react-chartjs-2';

const colours = [[120, 57, 136], [236, 151, 31]];

interface MultiSeriesChartWidgetProps {
  series: number[][];
}

interface MultiSeriesChartWidgetState {
  width: number;
  height: number;
}

// this control is a bit of a temporary hack, until I have a multi-series chart widget
export default class MultiSeriesChartWidget extends React.Component<MultiSeriesChartWidgetProps, MultiSeriesChartWidgetState> {
  private containerRef: React.RefObject<HTMLDivElement>;

  constructor(props: MultiSeriesChartWidgetProps) {
    super(props);
    this.state = { width: 0, height: 0 };
    this.containerRef = React.createRef();
    this.getDimensions = this.getDimensions.bind(this);
    this.renderChart = this.renderChart.bind(this);
  }

  componentDidMount() {
    this.getDimensions();
  }

  componentDidUpdate(prevProps: MultiSeriesChartWidgetProps, prevState: MultiSeriesChartWidgetState) {
    // Re-measure if dimensions are still 0 (e.g., after tab switch)
    if ((prevState.width === 0 && this.state.width === 0) ||
        (prevState.height === 0 && this.state.height === 0)) {
      this.getDimensions();
    }
  }

  getDimensions() {
    if (!this.containerRef.current) return;
    this.setState({
      width: this.containerRef.current.offsetWidth - 20,
      height: this.containerRef.current.offsetHeight - 10
    });
  }

  renderChart() {
    if (this.state.width === 0 || this.state.height === 0) {
      setTimeout(this.getDimensions, 0);
      return null;
    }

    const data = {
      labels: this.props.series[0].map(function(x) {
        return '';
      }),
      datasets: this.props.series.map((data, index) => {
        const colourString = colours[index % colours.length].join();
        return {
          label: '',

          backgroundColor: `rgba(${colourString},0.1)`,
          borderColor: `rgba(${colourString},1)`,
          data: data,
          pointRadius: 0
        };
      })
    };

    return (
      <Chart
        data={data}
        options={{
          animation: false,
          plugins: {
            legend: { display: false },
            tooltip: { enabled: false }
          },
          maintainAspectRatio: false,
          responsive: true,
          scales: {
            y: {
              ticks: { beginAtZero: true }
            }
          }
        }}
        width={this.state.width}
        height={this.state.height}
      />
    );
  }

  render() {
    return <div ref={this.containerRef} style={{ width: '100%', height: '100%', flex: 1 }}>{this.renderChart()}</div>;
  }
}
