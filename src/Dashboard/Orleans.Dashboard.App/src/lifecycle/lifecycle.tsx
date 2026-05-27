import React from 'react';
import mermaid from 'mermaid';
import Panel from '../components/panel';
import storage from '../lib/storage';

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

interface LifecycleState {
  startupSvg: string;
  shutdownSvg: string;
  rendering: boolean;
  errorMessage: string | null;
  activeTab: Direction;
}

let initialized = false;
function ensureMermaidInit() {
  if (initialized) return;
  const theme = storage.get('theme') === 'light' ? 'default' : 'dark';
  mermaid.initialize({
    startOnLoad: false,
    theme,
    securityLevel: 'loose',
    flowchart: { htmlLabels: true, curve: 'basis' }
  });
  initialized = true;
}

function escape(value: string): string {
  return (value || '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function buildStartupGraph(stages: LifecycleStageInfo[]): string {
  if (!stages.length) return 'flowchart TD\n  empty["No lifecycle data"]';

  const lines: string[] = [
    'flowchart TD',
    '  classDef stage fill:#374151,stroke:#9ca3af,color:#f9fafb,text-align:left;',
    '  classDef named fill:#1f2a44,stroke:#60a5fa,color:#f9fafb,text-align:left;'
  ];

  stages.forEach((s, idx) => {
    const id = `s${idx}`;
    const header = escape(`${s.stageName} (start)`);
    const body = s.observers
      .map(o => `&bull; ${escape(o.name)}${o.onStartMethod ? ` &mdash; ${escape(o.onStartMethod)}` : ''}`)
      .join('<br/>');
    lines.push(`  ${id}["<b>${header}</b><br/>${body || '<i>(no participants)</i>'}"]`);
    lines.push(`  class ${id} ${s.isNamedStage ? 'named' : 'stage'};`);
    if (idx > 0) {
      lines.push(`  s${idx - 1} --> ${id}`);
    }
  });

  return lines.join('\n');
}

function buildShutdownGraph(stages: LifecycleStageInfo[]): string {
  if (!stages.length) return 'flowchart TD\n  empty["No lifecycle data"]';

  const reversed = [...stages].reverse();
  const lines: string[] = [
    'flowchart TD',
    '  classDef stage fill:#4b1d1d,stroke:#fca5a5,color:#fef2f2,text-align:left;',
    '  classDef named fill:#3a0e0e,stroke:#f87171,color:#fef2f2,text-align:left;'
  ];

  reversed.forEach((s, idx) => {
    const id = `t${idx}`;
    const header = escape(`${s.stageName} (stop)`);
    const body = s.observers
      .map(o => `&bull; ${escape(o.name)}${o.onStopMethod ? ` &mdash; ${escape(o.onStopMethod)}` : ''}`)
      .join('<br/>');
    lines.push(`  ${id}["<b>${header}</b><br/>${body || '<i>(no participants)</i>'}"]`);
    lines.push(`  class ${id} ${s.isNamedStage ? 'named' : 'stage'};`);
    if (idx > 0) {
      lines.push(`  t${idx - 1} --> ${id}`);
    }
  });

  return lines.join('\n');
}

/**
 * Mermaid emits an SVG with fixed `width`/`height` attributes plus a
 * `max-width: NNNpx` style that, when the host container is narrower
 * than the diagram's natural width, leaves the rendered SVG element
 * at its full natural height while the content scales down — so the
 * bottom of the diagram gets clipped by the card. Replace those with
 * an explicit `aspect-ratio` derived from the viewBox so the browser
 * sizes the SVG element to match the scaled content.
 */
function normaliseSvg(svg: string): string {
  if (typeof DOMParser === 'undefined' || !svg) return svg;
  try {
    const doc = new DOMParser().parseFromString(svg, 'image/svg+xml');
    const svgEl = doc.documentElement as unknown as SVGSVGElement;
    if (!svgEl || svgEl.tagName.toLowerCase() !== 'svg') return svg;

    const viewBox = svgEl.getAttribute('viewBox');
    svgEl.removeAttribute('width');
    svgEl.removeAttribute('height');
    svgEl.removeAttribute('style');
    svgEl.style.display = 'block';
    svgEl.style.width = '100%';
    svgEl.style.maxWidth = '100%';
    svgEl.style.height = 'auto';
    if (viewBox) {
      const parts = viewBox.split(/\s+/).map(Number);
      if (parts.length === 4 && parts[2] > 0 && parts[3] > 0) {
        svgEl.style.aspectRatio = `${parts[2]} / ${parts[3]}`;
      }
    }
    return svgEl.outerHTML;
  } catch {
    return svg;
  }
}

export default class Lifecycle extends React.Component<LifecycleProps, LifecycleState> {
  state: LifecycleState = {
    startupSvg: '',
    shutdownSvg: '',
    rendering: false,
    errorMessage: null,
    activeTab: 'startup'
  };

  componentDidMount() {
    this.renderDiagrams();
  }

  componentDidUpdate(prevProps: LifecycleProps) {
    if (prevProps.stages !== this.props.stages) {
      this.renderDiagrams();
    }
  }

  async renderDiagrams() {
    if (!this.props.stages || !this.props.stages.length) return;
    ensureMermaidInit();
    this.setState({ rendering: true, errorMessage: null });
    try {
      const startup = buildStartupGraph(this.props.stages);
      const shutdown = buildShutdownGraph(this.props.stages);
      const { svg: startupSvg } = await mermaid.render('lifecycle-startup', startup);
      const { svg: shutdownSvg } = await mermaid.render('lifecycle-shutdown', shutdown);
      this.setState({
        startupSvg: normaliseSvg(startupSvg),
        shutdownSvg: normaliseSvg(shutdownSvg),
        rendering: false
      });
    } catch (err: any) {
      this.setState({
        rendering: false,
        errorMessage: (err && err.message) || String(err)
      });
    }
  }

  renderTable(direction: Direction) {
    const stages = this.props.stages || [];
    const ordered = direction === 'startup' ? stages : [...stages].reverse();
    return (
      <table className="table table-sm table-striped">
        <thead>
          <tr>
            <th style={{ width: '20%' }}>Stage</th>
            <th style={{ width: '25%' }}>Observer</th>
            <th>{direction === 'startup' ? 'OnStart' : 'OnStop'}</th>
          </tr>
        </thead>
        <tbody>
          {ordered.flatMap(s => {
            const observers = s.observers.length
              ? s.observers
              : ([{ name: '(no participants)', hasOnStart: false, hasOnStop: false }] as LifecycleObserverInfo[]);
            return observers.map((o, idx) => (
              <tr key={`${s.stage}-${o.name}-${idx}`}>
                {idx === 0 ? (
                  <td rowSpan={observers.length}>
                    <strong>{s.stageName}</strong>
                  </td>
                ) : null}
                <td>{o.name}</td>
                <td>
                  <code style={{ wordBreak: 'break-all' }}>
                    {direction === 'startup'
                      ? o.onStartMethod || (o.hasOnStart ? '(unknown)' : '—')
                      : o.onStopMethod || (o.hasOnStop ? '(unknown)' : '—')}
                  </code>
                </td>
              </tr>
            ));
          })}
        </tbody>
      </table>
    );
  }

  renderTab(direction: Direction) {
    const { startupSvg, shutdownSvg, rendering, errorMessage } = this.state;
    const svg = direction === 'startup' ? startupSvg : shutdownSvg;
    const subTitle = direction === 'startup'
      ? 'Low → high stage. Tasks within a stage start in parallel; stages execute sequentially.'
      : 'Reverse order — the highest started stage stops first.';
    const tableLabel = direction === 'startup' ? 'Startup observers' : 'Shutdown observers';
    const diagramLabel = direction === 'startup' ? 'Startup stages' : 'Shutdown stages';

    return (
      <div>
        <Panel title={diagramLabel} subTitle={subTitle}>
          <div>
            {errorMessage ? <pre className="text-danger">{errorMessage}</pre> : null}
            {rendering && !svg ? <p>Rendering…</p> : null}
            <div
              style={{ overflowX: 'auto', width: '100%' }}
              dangerouslySetInnerHTML={{ __html: svg }}
            />
          </div>
        </Panel>

        <Panel title={tableLabel}>{this.renderTable(direction)}</Panel>
      </div>
    );
  }

  selectTab = (direction: Direction) => () => this.setState({ activeTab: direction });

  render() {
    if (!this.props.stages) {
      return (
        <Panel title="Lifecycle">
          <p>Loading lifecycle data…</p>
        </Panel>
      );
    }

    if (!this.props.stages.length) {
      return (
        <Panel title="Lifecycle">
          <p>No active silo is available to report its lifecycle.</p>
        </Panel>
      );
    }

    const { activeTab } = this.state;
    const tabButton = (direction: Direction, label: string) => (
      <li className="nav-item" role="presentation">
        <button
          type="button"
          role="tab"
          aria-selected={activeTab === direction}
          className={`nav-link${activeTab === direction ? ' active' : ''}`}
          onClick={this.selectTab(direction)}
        >
          {label}
        </button>
      </li>
    );

    return (
      <div>
        <ul className="nav nav-tabs mb-3" role="tablist">
          {tabButton('startup', 'Startup')}
          {tabButton('shutdown', 'Shutdown')}
        </ul>
        {this.renderTab(activeTab)}
      </div>
    );
  }
}
