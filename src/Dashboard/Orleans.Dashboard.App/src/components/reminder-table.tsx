import React from 'react';

interface ReminderData {
  grainReference: string;
  primaryKey: string;
  activationCount: number;
  name: string;
  startAt: string;
  period: string;
}

interface ReminderTableProps {
  data?: ReminderData[];
}

interface ReminderTableState {
  grain_reference: string;
  primary_key: string;
  name: string;
  startAt: string;
  period: string;
}

export default class ReminderTable extends React.Component<ReminderTableProps, ReminderTableState> {
  constructor(props: ReminderTableProps) {
    super(props);
    this.state = {
      grain_reference: '',
      primary_key: '',
      name: '',
      startAt: '',
      period: ''
    };
    this.handleChange = this.handleChange.bind(this);
    this.renderReminder = this.renderReminder.bind(this);
    this.filterData = this.filterData.bind(this);
  }

  handleChange(e: React.ChangeEvent<HTMLInputElement>) {
    this.setState({
      [e.target.name]: e.target.value
    } as Pick<ReminderTableState, keyof ReminderTableState>);
  }

  renderReminder(reminderData: ReminderData, index: number) {
    return (
      <tr key={index}>
        <td>{reminderData.grainReference}</td>
        <td>{reminderData.primaryKey}</td>
        <td>
          <span className="pull-right">{reminderData.activationCount}</span>
        </td>
        <td>
          <span className="pull-right">{reminderData.name}</span>
        </td>
        <td>
          <span className="pull-right">
            {new Date(reminderData.startAt).toLocaleString()}
          </span>
        </td>
        <td>
          <span className="pull-right">{reminderData.period}</span>
        </td>
      </tr>
    );
  }

  filterData(data: ReminderData[]): ReminderData[] {
    return data
      .filter(x =>
        this.state['grain_reference']
          ? x.grainReference.indexOf(this.state['grain_reference']) > -1
          : x
      )
      .filter(x =>
        this.state['primary_key']
          ? x.primaryKey.indexOf(this.state['primary_key']) > -1
          : x
      )
      .filter(x =>
        this.state['name'] ? x.name.indexOf(this.state['name']) > -1 : x
      )
      .filter(x =>
        this.state['startAt']
          ? x.startAt.indexOf(this.state['startAt']) > -1
          : x
      )
      .filter(x =>
        this.state['period'] ? x.period.indexOf(this.state['period']) > -1 : x
      );
  }

  render() {
    if (!this.props.data) return null;
    const filteredData = this.filterData(this.props.data);
    return (
      <table className="table">
        <tbody>
          <tr>
            <th style={{ textAlign: 'left' }}>Grain Reference</th>
            <th>Primary Key</th>
            <th />
            <th style={{ textAlign: 'left' }}>Name</th>
            <th style={{ textAlign: 'left' }}>Start At</th>
            <th style={{ textAlign: 'right' }}>Period</th>
          </tr>
          <tr>
            <th style={{ textAlign: 'left' }}>
              <input
                onChange={this.handleChange}
                value={this.state['grain_reference']}
                type="text"
                name="grain_reference"
                className="form-control"
                placeholder="Filter by Grain Reference"
              />
            </th>
            <th style={{ textAlign: 'left' }}>
              <input
                onChange={this.handleChange}
                value={this.state['primary_key']}
                type="text"
                name="primary_key"
                className="form-control"
                placeholder="Filter by Primary Key"
              />
            </th>
            <th />
            <th style={{ textAlign: 'left' }}>
              <input
                onChange={this.handleChange}
                value={this.state['name']}
                type="text"
                name="name"
                className="form-control"
                placeholder="Filter by Name"
              />
            </th>
            <th style={{ textAlign: 'left' }}>
              <input
                onChange={this.handleChange}
                value={this.state['startAt']}
                type="text"
                name="startAt"
                className="form-control"
                placeholder="Filter by Start At"
              />
            </th>
            <th style={{ textAlign: 'right' }}>
              <input
                onChange={this.handleChange}
                value={this.state['period']}
                type="text"
                name="period"
                className="form-control"
                placeholder="Filter by Period"
              />
            </th>
          </tr>
          {filteredData.map(this.renderReminder)}
        </tbody>
      </table>
    );
  }
}
