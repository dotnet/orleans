import React from 'react';
import Page from '../components/page';
import http from '../lib/http';
import DisplayGrainState from '../components/display-grain-state';
import Panel from '../components/panel';

interface GrainDetailsProps {
  grainTypes: string[];
}

interface GrainDetailsState {
  grainId: string;
  grainType: string | null;
  grainState: string;
}

export default class GrainDetails extends React.Component<GrainDetailsProps, GrainDetailsState> {
  constructor(props: GrainDetailsProps) {
    super(props);
    this.state = { grainId: '', grainType: null, grainState: '' };
    this.handleGrainIdChange = this.handleGrainIdChange.bind(this);
    this.handleGrainTypeChange = this.handleGrainTypeChange.bind(this);
    this.handleSubmit = this.handleSubmit.bind(this);
  }

  handleGrainIdChange(event: React.ChangeEvent<HTMLInputElement>) {
    this.setState({ grainId: event.target.value });
  }

  handleGrainTypeChange(event: React.ChangeEvent<HTMLSelectElement>) {
    this.setState({ grainType: event.target.value });
  }

  handleSubmit(event: React.MouseEvent<HTMLInputElement>) {
    const component = this;

    http.get('GrainState?grainId=' + this.state.grainId + '&grainType=' + this.state.grainType, function (err, data) {
      component.setState({ grainState: data });
    }).then(() => {

    });

    event.preventDefault();
  }

  renderEmpty() {
    return <span>No state retrieved</span>;
  }

  renderState() {
    let displayComponent: React.ReactNode;

    if (this.state.grainState !== '') {
      displayComponent = <DisplayGrainState code={this.state.grainState} />;
    } else {
      displayComponent = <div></div>;
    }

    return (
      <Page
        title="Grain Details"
      >
        <div>
          <Panel title='Grain' subTitle="Only non generic grains are supported">
            <div className="row">
              <div className="col-md-6 col-lg-6 col-xl-6">
                <div className="input-group">
                  <select value={this.state.grainType || ''} className="form-control" onChange={this.handleGrainTypeChange}>
                    <option disabled value=""> -- Select an grain type -- </option>
                    {
                      this.props.grainTypes.map((_item) => <option key={_item} value={_item}>{_item}</option>)
                    }
                  </select>
                </div>
              </div>
              <div className="col-md-4 col-lg-4 col-xl-5">
                <div className="input-group">
                  <input type="text" placeholder='Grain Id' className="form-control"
                    value={this.state.grainId} onChange={this.handleGrainIdChange} />
                </div>
              </div>
              <div className="col-md-2 col-lg-2 col-xl-1">
                <div className="input-group">
                  <input type="button" value="Show Details" className='btn btn-default btn-block' onClick={this.handleSubmit} />
                </div>
              </div>
            </div>
          </Panel>

          <Panel title='State' bodyPadding='0px'>
            <div className="row">
              <div className="col-md-12">
                {displayComponent}
              </div>
            </div>
          </Panel>
        </div>
      </Page>
    );
  }

  render() {
    return this.renderState();
  }
}
