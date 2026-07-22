import React from 'react';

interface AdvancedReminderData {
  grainReference: string;
  primaryKey: string;
  name: string;
  startAt: string;
  period: string;
  cronExpression?: string;
  cronTimeZoneId?: string;
  nextDueUtc?: string | null;
  lastFireUtc?: string | null;
  priority?: string;
  missedAction?: string;
}

interface AdvancedReminderTableProps {
  data?: AdvancedReminderData[];
}

interface AdvancedReminderTableState {
  grainReference: string;
  primaryKey: string;
  name: string;
  schedule: string;
  startAt: string;
  nextDueUtc: string;
  lastFireUtc: string;
  priority: string;
  missedAction: string;
}

export default class AdvancedReminderTable extends React.Component<AdvancedReminderTableProps, AdvancedReminderTableState> {
  constructor(props: AdvancedReminderTableProps) {
    super(props);
    this.state = {
      grainReference: '',
      primaryKey: '',
      name: '',
      schedule: '',
      startAt: '',
      nextDueUtc: '',
      lastFireUtc: '',
      priority: '',
      missedAction: ''
    };
    this.handleChange = this.handleChange.bind(this);
    this.renderReminder = this.renderReminder.bind(this);
  }

  handleChange(e: React.ChangeEvent<HTMLInputElement>) {
    this.setState({
      [e.target.name]: e.target.value
    } as Pick<AdvancedReminderTableState, keyof AdvancedReminderTableState>);
  }

  renderFilter(name: keyof AdvancedReminderTableState, placeholder: string) {
    return (
      <input
        onChange={this.handleChange}
        value={this.state[name]}
        type="text"
        name={name}
        className="form-control form-control-sm"
        placeholder={placeholder}
      />
    );
  }

  getSchedule(reminder: AdvancedReminderData) {
    if (reminder.cronExpression) {
      return reminder.cronTimeZoneId
        ? `${reminder.cronExpression} (${reminder.cronTimeZoneId})`
        : reminder.cronExpression;
    }

    return reminder.period;
  }

  formatDate(value?: string | null) {
    return value ? new Date(value).toLocaleString() : '—';
  }

  includes(value: string | undefined | null, filter: string) {
    return !filter || (value || '').toLocaleLowerCase().includes(filter.toLocaleLowerCase());
  }

  renderReminder(reminder: AdvancedReminderData, index: number) {
    const schedule = this.getSchedule(reminder);
    return (
      <tr key={`${reminder.grainReference}-${reminder.name}-${index}`}>
        <td title={reminder.grainReference}>{reminder.grainReference}</td>
        <td title={reminder.primaryKey}>{reminder.primaryKey}</td>
        <td title={reminder.name}>{reminder.name}</td>
        <td title={schedule}>
          {reminder.cronExpression ? <code>{schedule}</code> : schedule}
        </td>
        <td>{this.formatDate(reminder.startAt)}</td>
        <td>{this.formatDate(reminder.nextDueUtc)}</td>
        <td>{this.formatDate(reminder.lastFireUtc)}</td>
        <td>{reminder.priority || '—'}</td>
        <td>{reminder.missedAction || '—'}</td>
      </tr>
    );
  }

  filterData(data: AdvancedReminderData[]): AdvancedReminderData[] {
    return data.filter(reminder =>
      this.includes(reminder.grainReference, this.state.grainReference) &&
      this.includes(reminder.primaryKey, this.state.primaryKey) &&
      this.includes(reminder.name, this.state.name) &&
      this.includes(this.getSchedule(reminder), this.state.schedule) &&
      this.includes(this.formatDate(reminder.startAt), this.state.startAt) &&
      this.includes(this.formatDate(reminder.nextDueUtc), this.state.nextDueUtc) &&
      this.includes(this.formatDate(reminder.lastFireUtc), this.state.lastFireUtc) &&
      this.includes(reminder.priority, this.state.priority) &&
      this.includes(reminder.missedAction, this.state.missedAction)
    );
  }

  render() {
    if (!this.props.data) return null;
    const filteredData = this.filterData(this.props.data);
    return (
      <div className="table-responsive">
        <table className="table table-striped advanced-reminder-table">
          <colgroup>
            <col className="advanced-reminder-grain-column" />
            <col className="advanced-reminder-key-column" />
            <col className="advanced-reminder-name-column" />
            <col className="advanced-reminder-schedule-column" />
            <col className="advanced-reminder-date-column" />
            <col className="advanced-reminder-date-column" />
            <col className="advanced-reminder-date-column" />
            <col className="advanced-reminder-priority-column" />
            <col className="advanced-reminder-action-column" />
          </colgroup>
          <thead>
            <tr>
              <th>Grain Reference</th>
              <th>Primary Key</th>
              <th>Name</th>
              <th>Schedule</th>
              <th>Start At</th>
              <th>Next Due</th>
              <th>Last Fired</th>
              <th>Priority</th>
              <th>Missed Action</th>
            </tr>
            <tr>
              <th>{this.renderFilter('grainReference', 'Filter grain')}</th>
              <th>{this.renderFilter('primaryKey', 'Filter key')}</th>
              <th>{this.renderFilter('name', 'Filter name')}</th>
              <th>{this.renderFilter('schedule', 'Filter schedule')}</th>
              <th>{this.renderFilter('startAt', 'Filter start')}</th>
              <th>{this.renderFilter('nextDueUtc', 'Filter next due')}</th>
              <th>{this.renderFilter('lastFireUtc', 'Filter last fired')}</th>
              <th>{this.renderFilter('priority', 'Priority')}</th>
              <th>{this.renderFilter('missedAction', 'Action')}</th>
            </tr>
          </thead>
          <tbody>{filteredData.map(this.renderReminder)}</tbody>
        </table>
      </div>
    );
  }
}
