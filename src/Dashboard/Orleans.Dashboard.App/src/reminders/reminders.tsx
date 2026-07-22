import React from 'react';
import CounterWidget from '../components/counter-widget';
import AdvancedReminderTable from '../components/advanced-reminder-table';
import ReminderTable from '../components/reminder-table';
import Panel from '../components/panel';

interface Reminder {
  [key: string]: any;
}

interface RemindersData {
  count: number;
  hasMore?: boolean;
  reminders: Reminder[];
}

interface RemindersProps {
  remindersData: RemindersData;
  reminderKind: 'classic' | 'advanced';
  page: number;
}

export default class Reminders extends React.Component<RemindersProps> {
  getPageHref(page: number) {
    return this.props.reminderKind === 'advanced'
      ? `#/reminders/advanced/${page}`
      : `#/reminders/${page}`;
  }

  render() {
    const isAdvanced = this.props.reminderKind === 'advanced';
    const totalPages = Math.ceil(this.props.remindersData.count / 50);
    const showFirst = this.props.page > 2;
    const showPrevious = this.props.page > 1;
    const showNext = isAdvanced
      ? this.props.remindersData.hasMore === true
      : totalPages > this.props.page;
    return (
      <div>
        {!isAdvanced ? (
          <div className="row">
            <div className="col-md-12">
              <CounterWidget
                icon="calendar"
                counter={this.props.remindersData.count}
                title="Classic Reminders Count"
              />
            </div>
          </div>
        ) : null}
        <div className="card reminder-table-switch">
          <div className="card-body">
            <div className="btn-group" role="group" aria-label="Reminder table">
              <a
                className={`btn btn-default ${
                  isAdvanced ? '' : 'reminder-table-active'
                }`}
                href="#/reminders/1"
                aria-current={isAdvanced ? undefined : 'page'}
              >
                Classic Reminders
              </a>
              <a
                className={`btn btn-default ${
                  isAdvanced ? 'reminder-table-active' : ''
                }`}
                href="#/reminders/advanced/1"
                aria-current={isAdvanced ? 'page' : undefined}
              >
                Advanced Reminders
              </a>
            </div>
          </div>
        </div>
        {isAdvanced ? (
          <Panel title="Advanced Reminders" subTitle={`Page ${this.props.page}`}>
            <AdvancedReminderTable data={this.props.remindersData.reminders} />
          </Panel>
        ) : (
          <Panel title="Classic Reminders" subTitle={`Page ${this.props.page}`}>
            <ReminderTable data={this.props.remindersData.reminders} />
          </Panel>
        )}
        {showPrevious || showNext ? (
          <div className="card">
            <div className="card-body">
              <div style={{ textAlign: 'center' }}>
                {showFirst ? (
                  <a
                    className="btn btn-default bg-purple"
                    href={this.getPageHref(1)}
                  >
                    <i className="fa fa-arrow-circle-left" /> First
                  </a>
                ) : null}
                <span> </span>
                {showPrevious ? (
                  <a
                    className="btn btn-default bg-purple"
                    href={this.getPageHref(this.props.page - 1)}
                  >
                    <i className="fa fa-arrow-circle-left" /> Previous
                  </a>
                ) : null}
                <span> </span>
                {showNext ? (
                  <a
                    className="btn btn-default bg-purple"
                    href={this.getPageHref(this.props.page + 1)}
                  >
                    Next <i className="fa fa-arrow-circle-right" />
                  </a>
                ) : null}
              </div>
            </div>
          </div>
        ) : null}
      </div>
    );
  }
}
