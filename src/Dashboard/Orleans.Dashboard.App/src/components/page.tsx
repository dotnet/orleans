import React from 'react';

interface PageProps {
  title: string;
  subTitle?: React.ReactNode;
  children: React.ReactNode;
}

export default class Page extends React.Component<PageProps> {
  render() {
    return (
      <div>
        <div className="app-content-header">
          <div className="container-fluid">
            <div className="row mb-2">
              <div className="col-sm-12">
                <h1 className="m-0">
                  {this.props.title} <small> {this.props.subTitle}</small>
                </h1>
              </div>
            </div>
          </div>
        </div>
        <div className="app-content">
          <div className="container-fluid">
            {this.props.children}
          </div>
        </div>
      </div>
    );
  }
}
