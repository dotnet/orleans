import React from 'react';

interface DisplayGrainStateProps {
  code: string;
}

export default class DisplayGrainState extends React.Component<DisplayGrainStateProps> {
  constructor(props: DisplayGrainStateProps) {
    super(props);
  }

  render() {
    return (
      <pre style={{ margin: '0 0' }}>
        {this.props.code}
      </pre>
    );
  }
}
