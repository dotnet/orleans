import React from 'react';

interface PanelProps {
  title: string;
  subTitle?: string;
  children: React.ReactNode;
  bodyPadding?: string;
}

export default class Panel extends React.Component<PanelProps> {
  render() {
    let body: React.ReactNode;
    let footer: React.ReactNode;

    if (Array.isArray(this.props.children) && this.props.children.length) {
      body = this.props.children[0];
      footer = (
        <div className="card-footer clearfix">{this.props.children[1]}</div>
      );
    } else {
      body = this.props.children;
      footer = null;
    }

    const bodyStyle: React.CSSProperties = {};
    if (this.props.bodyPadding) {
      bodyStyle.padding = this.props.bodyPadding;
    }
    return (
      <div className="card">
        <div className="card-header">
          <h3 className="card-title">
            {this.props.title}
            <small style={{ marginLeft: '10px' }}>{this.props.subTitle}</small>
          </h3>
        </div>
        <div className="card-body" style={bodyStyle}>{body}</div>
        {footer}
      </div>
    );
  }
}
