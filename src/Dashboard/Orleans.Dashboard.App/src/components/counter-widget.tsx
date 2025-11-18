import React from 'react';

interface CounterWidgetProps {
  icon: string;
  title: string;
  counter: number | string;
  link?: string;
  style?: React.CSSProperties;
}

export default class CounterWidget extends React.Component<CounterWidgetProps> {
  constructor(props: CounterWidgetProps) {
    super(props);
    this.renderMore = this.renderMore.bind(this);
  }

  renderMore() {
    if (!this.props.link) return null;
    return (
      <a href={this.props.link} className="small-box-footer">
        More info <i className="fa fa-arrow-circle-right" />
      </a>
    );
  }

  render() {
    return (
      <div className="info-box" style={this.props.style}>
        <span className="info-box-icon bg-purple">
          <i className={`fa fa-${this.props.icon}`} />
        </span>
        <div className="info-box-content">
          <span className="info-box-text">{this.props.title}</span>
          <span className="info-box-number">{this.props.counter}</span>
          {this.renderMore()}
        </div>
      </div>
    );
  }
}
