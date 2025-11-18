import React from 'react';

interface AlertProps {
  onClose?: () => void;
  title?: string;
  children?: React.ReactNode;
}

export default class Alert extends React.Component<AlertProps> {
  constructor(props: AlertProps) {
    super(props);
    this.handleClick = this.handleClick.bind(this);
  }

  handleClick() {
    if (this.props.onClose) {
      this.props.onClose();
    }
  }

  render() {
    return (
      <div className="alert alert-danger alert-dismissible">
        <button
          type="button"
          className="close"
          onClick={this.handleClick}
        >
          ×
        </button>
        <h4>
          <i className="icon fa fa-ban" /> {this.props.title || 'Error'}
        </h4>
        {this.props.children}.
      </div>
    );
  }
}
