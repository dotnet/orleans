import React from 'react';

interface LogStreamProps {
  xhr: XMLHttpRequest;
}

interface LogStreamState {
  log: string;
  filter: string;
  scrollEnabled: boolean;
  filterRegex?: RegExp;
}

export default class LogStream extends React.Component<LogStreamProps, LogStreamState> {
  private logRef: React.RefObject<HTMLPreElement>;

  constructor(props: LogStreamProps) {
    super(props);
    this.state = {
      log: 'Connecting...',
      filter: '',
      scrollEnabled: true
    };
    this.logRef = React.createRef();
    this.scroll = this.scroll.bind(this);
    this.onProgress = this.onProgress.bind(this);
    this.toggle = this.toggle.bind(this);
    this.filterChanged = this.filterChanged.bind(this);
    this.getFilteredLog = this.getFilteredLog.bind(this);
  }

  scroll() {
    if (this.logRef.current) {
      this.logRef.current.scrollTop = this.logRef.current.scrollHeight;
    }
  }

  onProgress() {
    if (!this.state.scrollEnabled) return;
    this.setState(
      {
        log: this.props.xhr.responseText
      },
      this.scroll
    );
  }

  componentDidMount() {
    this.props.xhr.onprogress = this.onProgress;
  }

  componentWillUnmount() {
    this.props.xhr.abort();
  }

  toggle() {
    this.setState({
      scrollEnabled: !this.state.scrollEnabled
    });
  }

  filterChanged(event: React.ChangeEvent<HTMLInputElement>) {
    this.setState({
      filter: event.target.value,
      filterRegex: new RegExp(
        `[^\\s-]* (Trace|Debug|Information|Warning|Error):.*${
          event.target.value
        }.*`,
        'gmi'
      )
    });
  }

  getFilteredLog(): string {
    if (!this.state.filter) return this.state.log;

    const matches = this.state.log.match(this.state.filterRegex);
    return matches ? matches.join('\r\n') : '';
  }

  render() {
    return (
      <div
        style={{
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
          maxHeight: '100%'
        }}
      >
        <input
          type="search"
          name="filter"
          className="text log-filter"
          style={{ width: '100%', height: '40px' }}
          value={this.state.filter}
          onChange={this.filterChanged}
          placeholder={'Regex Filter'}
        />
        <pre
          ref={this.logRef}
          className="log"
          style={{
            overflowY: 'auto',
            width: '100%',
            height: 'calc(100vh - 100px)',
            whiteSpace: 'pre-wrap'
          }}
        >
          {this.getFilteredLog()}
        </pre>
        <a
          href="javascript:void"
          onClick={this.toggle}
          className="btn btn-default"
          style={{
            marginLeft: '-80px',
            position: 'fixed',
            top: '95px',
            left: '100%'
          }}
        >
          {this.state.scrollEnabled ? 'Pause' : 'Resume'}
        </a>
      </div>
    );
  }
}
