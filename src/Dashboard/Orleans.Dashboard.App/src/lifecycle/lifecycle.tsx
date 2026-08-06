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
    securityLevel: 'strict',
    flowchart: { htmlLabels: false, curve: 'basis' }
  });
  initialized = true;
}

function escape(value: string): string {
  return (value || '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/[\r\n]+/g, ' ');
}

function formatStageLabel(stage: LifecycleStageInfo, direction: Direction): string {
  const title = `${escape(stage.stageName)} (${direction === 'startup' ? 'start' : 'stop'})`;
  const observerLines = stage.observers.map(observer => {
    const method = direction === 'startup' ? observer.onStartMethod : observer.onStopMethod;
    return method
      ? `- ${escape(observer.name)} - ${escape(method)}`
      : `- ${escape(observer.name)}`;
  });

  return [title, ...(observerLines.length ? observerLines : ['(no participants)'])].join('\\n');
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
    lines.push(`  ${id}["${formatStageLabel(s, 'startup')}"]`);
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
    lines.push(`  ${id}["${formatStageLabel(s, 'shutdown')}"]`);
    lines.push(`  class ${id} ${s.isNamedStage ? 'named' : 'stage'};`);
    if (idx > 0) {
      lines.push(`  t${idx - 1} --> ${id}`);
    }
  });

  return lines.join('\n');
}

/**
 * Strip Mermaid's fixed width/height/max-width styling so the subsequent
 * `fixSvgViewBox` pass (which runs after layout) can install its own
 * intrinsic dimensions derived from the actual rendered bounds.
 */
function normaliseSvg(svg: string): string {
  if (typeof DOMParser === 'undefined' || !svg) return svg;
  try {
    const doc = new DOMParser().parseFromString(svg, 'image/svg+xml');
    const svgEl = doc.documentElement as unknown as SVGSVGElement;
    if (!svgEl || svgEl.tagName.toLowerCase() !== 'svg') return svg;

    svgEl.removeAttribute('width');
    svgEl.removeAttribute('height');
    svgEl.removeAttribute('style');
    svgEl.style.display = 'block';
    return svgEl.outerHTML;
  } catch {
    return svg;
  }
}

/**
 * Once the SVG is mounted in the DOM, `getBBox()` returns the union of
 * the actual rendered bounds of every node. Rewrite the viewBox to that
 * bounding box (with a small padding) and install explicit `width`/`height`
 * attributes so the SVG renders at natural pixel size. The surrounding
 * wrapper provides scrollbars when the diagram is larger than the host card.
 */
function fixSvgViewBox(container: HTMLElement | null): void {
  if (!container) return;
  const svg = container.querySelector('svg') as SVGSVGElement | null;
  if (!svg) return;

  // getBBox throws on detached/unrendered SVGs; ignore failures.
  let bbox: { x: number; y: number; width: number; height: number };
  try {
    const raw = svg.getBBox();
    bbox = { x: raw.x, y: raw.y, width: raw.width, height: raw.height };
  } catch {
    return;
  }

  if (bbox.width <= 0 || bbox.height <= 0) return;

  const padding = 16;
  const x = bbox.x - padding;
  const y = bbox.y - padding;
  const w = Math.ceil(bbox.width + padding * 2);
  const h = Math.ceil(bbox.height + padding * 2);

  svg.setAttribute('viewBox', `${x} ${y} ${w} ${h}`);
  svg.setAttribute('width', String(w));
  svg.setAttribute('height', String(h));
  svg.style.aspectRatio = '';
  svg.style.maxWidth = '';
  svg.style.height = '';
}

export default class Lifecycle extends React.Component<LifecycleProps, LifecycleState> {
  state: LifecycleState = {
    startupSvg: '',
    shutdownSvg: '',
    rendering: false,
    errorMessage: null,
    activeTab: 'startup'
  };

  private diagramRef = React.createRef<HTMLDivElement>();

  componentDidMount() {
    this.renderDiagrams();
  }

  componentDidUpdate(prevProps: LifecycleProps, prevState: LifecycleState) {
    if (prevProps.stages !== this.props.stages) {
      this.renderDiagrams();
    }

    const svgChanged =
      prevState.startupSvg !== this.state.startupSvg ||
      prevState.shutdownSvg !== this.state.shutdownSvg ||
      prevState.activeTab !== this.state.activeTab;

    if (svgChanged) {
      // Wait one frame so the browser has flushed layout for the freshly
      // inserted SVG before we measure it with getBBox().
      requestAnimationFrame(() => fixSvgViewBox(this.diagramRef.current));
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
              ref={this.diagramRef}
              style={{ overflow: 'auto', width: '100%', maxHeight: '80vh' }}
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
