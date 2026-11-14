import React from 'react';
import Panel from '../components/panel';
import './lifecycle.css';

export interface LifecycleObserverInfo {
  name: string;
  observerType?: string;
  hasOnStart: boolean;
  hasOnStop: boolean;
  onStartMethod?: string;
  onStopMethod?: string;
}

export interface LifecycleStageInfo {
  stage: number;
  stageName: string;
  isNamedStage: boolean;
  observers: LifecycleObserverInfo[];
}

type Direction = 'startup' | 'shutdown';

interface LifecycleProps {
  stages: LifecycleStageInfo[] | null;
}

export default function Lifecycle({ stages }: LifecycleProps) {
  const [activeTab, setActiveTab] = React.useState<Direction>('startup');

  if (!stages) {
    return <Panel title="Lifecycle"><p>Loading lifecycle data…</p></Panel>;
  }
  if (!stages.length) {
    return <Panel title="Lifecycle"><p>No active silo is available to report its lifecycle.</p></Panel>;
  }

  const startup = activeTab === 'startup';
  const ordered = startup ? stages : [...stages].reverse();
  const diagramLabel = startup ? 'Startup stages' : 'Shutdown stages';
  const tableLabel = startup ? 'Startup observers' : 'Shutdown observers';
  const subTitle = startup
    ? 'Low → high stage. Tasks within a stage start in parallel; stages execute sequentially.'
    : 'Reverse order — the highest started stage stops first.';

  return (
    <div>
      <ul className="nav nav-tabs mb-3" role="tablist">
        {(['startup', 'shutdown'] as const).map(direction => (
          <li className="nav-item" role="presentation" key={direction}>
            <button
              type="button"
              role="tab"
              aria-selected={activeTab === direction}
              className={`nav-link${activeTab === direction ? ' active' : ''}`}
              onClick={() => setActiveTab(direction)}
            >
              {direction === 'startup' ? 'Startup' : 'Shutdown'}
            </button>
          </li>
        ))}
      </ul>

      <Panel title={diagramLabel} subTitle={subTitle}>
        <div className="lifecycle-diagram" tabIndex={0} role="region" aria-label={diagramLabel}>
          <ol className={`lifecycle-stages ${activeTab}`} aria-label={diagramLabel}>
            {ordered.map(stage => (
              <li key={stage.stage} className={`lifecycle-stage${stage.isNamedStage ? ' named' : ''}`}>
                <strong>{stage.stageName} ({startup ? 'start' : 'stop'})</strong>
                {stage.observers.length ? stage.observers.map((observer, index) => {
                  const method = startup ? observer.onStartMethod : observer.onStopMethod;
                  return <div key={index}>- {observer.name}{method ? ` - ${method}` : ''}</div>;
                }) : <div>(no participants)</div>}
              </li>
            ))}
          </ol>
        </div>
      </Panel>

      <Panel title={tableLabel}>
        <table className="table table-sm table-striped">
          <thead>
            <tr>
              <th style={{ width: '20%' }}>Stage</th>
              <th style={{ width: '25%' }}>Observer</th>
              <th>{startup ? 'OnStart' : 'OnStop'}</th>
            </tr>
          </thead>
          <tbody>
            {ordered.flatMap(stage => {
              const observers: LifecycleObserverInfo[] = stage.observers.length
                ? stage.observers
                : [{ name: '(no participants)', hasOnStart: false, hasOnStop: false }];
              return observers.map((observer, index) => {
                const method = startup ? observer.onStartMethod : observer.onStopMethod;
                const hasMethod = startup ? observer.hasOnStart : observer.hasOnStop;
                return (
                  <tr key={`${stage.stage}-${index}`}>
                    {index === 0 ? <td rowSpan={observers.length}><strong>{stage.stageName}</strong></td> : null}
                    <td>{observer.name}</td>
                    <td><code style={{ wordBreak: 'break-all' }}>{method || (hasMethod ? '(unknown)' : '—')}</code></td>
                  </tr>
                );
              });
            })}
          </tbody>
        </table>
      </Panel>
    </div>
  );
}
