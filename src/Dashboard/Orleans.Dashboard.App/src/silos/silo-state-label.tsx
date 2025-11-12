import React from 'react';

type SiloStatus = 'Created' | 'Joining' | 'Active' | 'ShuttingDown' | 'Stopping' | 'Dead';

const labelClassMapper: { [key in SiloStatus]: string } = {
  Created: 'info',
  Joining: 'info',
  Active: 'success',
  ShuttingDown: 'warning',
  Stopping: 'warning',
  Dead: 'danger'
};

interface SiloStateLabelProps {
  status: string;
}

export default class SiloStateLabel extends React.Component<SiloStateLabelProps> {
  render() {
    const status = this.props.status as SiloStatus;
    return (
      <span className={'label label-' + (labelClassMapper[status] || 'default')}>
        {this.props.status || 'unknown'}
      </span>
    );
  }
}
