import React from 'react';

interface PropertiesWidgetProps {
  data: { [key: string]: any };
}

export default class PropertiesWidget extends React.Component<PropertiesWidgetProps> {
  constructor(props: PropertiesWidgetProps) {
    super(props);
    this.renderRow = this.renderRow.bind(this);
  }

  renderRow(key: string) {
    return (
      <tr key={key}>
        <td style={{ textOverflow: 'ellipsis' }}>{key}</td>
        <td style={{ textAlign: 'right' }}>
          <strong>{this.props.data[key]}</strong>
        </td>
      </tr>
    );
  }

  render() {
    return (
      <table className="table">
        <tbody>{Object.keys(this.props.data).map(this.renderRow)}</tbody>
      </table>
    );
  }
}
