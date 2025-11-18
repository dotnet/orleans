import React from 'react';

export default class Loading extends React.Component {
  render() {
    return (
      <section className="content" style={{ height: '100vh' }}>
        <span style={{ paddingTop: '25px' }}>
          <i className="fa fa-spinner fa-pulse fa-fw" />Loading...
        </span>
      </section>
    );
  }
}
