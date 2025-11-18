// Import styles
import './styles.css';

// Register Chart.js components
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  PointElement,
  LineElement,
  ArcElement,
  Title,
  Tooltip,
  Legend,
  Filler
} from 'chart.js';

ChartJS.register(
  CategoryScale,
  LinearScale,
  PointElement,
  LineElement,
  ArcElement,
  Title,
  Tooltip,
  Legend,
  Filler
);

import http from './lib/http';
import React from 'react';
import ReactDom from 'react-dom';
import routie from './lib/routie';
import Silo from './silos/silo';
import events from 'eventthing';
import Grain from './grains/grain';
import GrainDetails from './grains/grain-details';
import Page from './components/page';
import Loading from './components/loading';
import Menu from './components/menu';
import BrandHeader from './components/brand-header';
import Grains from './grains/grains';
import Silos from './silos/silos';
import Overview from './overview/overview';
import SiloState from './silos/silo-state-label';
import Alert from './components/alert';
import LogStream from './logstream/log-stream';
import SiloCounters from './silos/silo-counters';
import Reminders from './reminders/reminders';
import Preferences from './components/preferences';
import storage from './lib/storage';

interface Settings {
  dashboardGrainsHidden: boolean;
  systemGrainsHidden: boolean;
}

interface MenuItem {
  name: string;
  path: string;
  icon: string;
  active?: boolean;
  isSeparated?: boolean;
}

interface GrainStatItem {
  grain?: string;
  grainType?: string;
}

const target = document.getElementById('content');

// Restore theme preference.
let defaultTheme = storage.get('theme') || 'dark';
if (defaultTheme === 'dark') {
  document.getElementById('body')!.classList.add('dark-mode');
  dark();
} else {
  light();
}

// Restore grain visibility preferences.
let settings: Settings = {
  dashboardGrainsHidden: storage.get('dashboardGrains') === 'hidden',
  systemGrainsHidden: storage.get('systemGrains') === 'hidden'
};

// Global state.
let dashboardCounters: any = {};
let unfilteredDashboardCounters: any = {};
let routeIndex = 0;

function scroll() {
  try {
    document.getElementsByClassName('wrapper')[0].scrollTo(0, 0);
  } catch (e) { }
}

let errorTimer: NodeJS.Timeout | null;
function showError(message: string) {
  ReactDom.render(
    <Alert onClose={closeError}>{message}</Alert>,
    document.getElementById('error-message-content')
  );
  if (errorTimer) clearTimeout(errorTimer);
  errorTimer = setTimeout(closeError, 3000);
}

function closeError() {
  if (errorTimer) clearTimeout(errorTimer);
  errorTimer = null;
  ReactDom.render(<span />, document.getElementById('error-message-content'));
}

http.onError(showError);

function setIntervalDebounced(action: () => Promise<any>, interval: number) {
  Promise.resolve(action()).finally(() => {
    setTimeout(setIntervalDebounced.bind(this, action, interval), interval);
  });
}

// continually poll the dashboard counters
function loadDashboardCounters() {
  return http.get('DashboardCounters', function (err, data) {
    dashboardCounters = data;
    unfilteredDashboardCounters = data;
    dashboardCounters.simpleGrainStats = unfilteredDashboardCounters.simpleGrainStats.filter(
      getFilter(settings)
    );
    events.emit('dashboard-counters', dashboardCounters);
  });
}

function getVersion() {
  let version = '2';
  const renderVersion = function () {
    ReactDom.render(
      <span id="version" style={{ marginLeft: 40 }}>
        v.{version}
        <i
          style={{ marginLeft: '12px', marginRight: '5px' }}
          className="fab fa-github"
        />
        <a
          style={{ color: '#b8c7ce', textDecoration: 'underline' }}
          href="https://github.com/OrleansContrib/OrleansDashboard/"
        >
          Source
        </a>
      </span>,
      document.getElementById('version-content')
    );
  };

  const loadData = function (cb?: any) {
    http.get('version', function (err, data) {
      version = data.version;
      renderVersion();
    });
  };
  loadData();
}

// we always want to refresh the dashboard counters
setIntervalDebounced(loadDashboardCounters, 1000);
loadDashboardCounters();
let render: () => void = () => { };

function renderLoading() {
  ReactDom.render(<Loading />, target);
}

const menuElement = document.getElementById('menu');
const brandHeaderElement = document.getElementById('brand-header');

// Render brand header once
ReactDom.render(<BrandHeader />, brandHeaderElement);

function renderPage(jsx: JSX.Element, path: string) {
  ReactDom.render(jsx, target);
  const menu = getMenu();
  menu.forEach(x => {
    x.active = x.path === path;
  });

  ReactDom.render(<Menu menu={menu} />, menuElement);
}

(routie as any)('', function () {
  const thisRouteIndex = ++routeIndex;
  events.clearAll();
  scroll();
  renderLoading();

  let clusterStats: any = {};
  let grainMethodStats: any = [];
  let unfiltedMethodStats: any = [];
  let loadDataIsPending = false;
  const loadData = function (cb?: any) {
    if (!loadDataIsPending) {
      loadDataIsPending = true;
      http.get('ClusterStats', function (err, data) {
        clusterStats = data;
        http.get('TopGrainMethods', function (err, grainMethodsData) {
          grainMethodStats = grainMethodsData;
          unfiltedMethodStats = grainMethodsData;
          grainMethodStats.calls = unfiltedMethodStats.calls.filter(
            getFilter(settings)
          );
          grainMethodStats.errors = unfiltedMethodStats.errors.filter(
            getFilter(settings)
          );
          grainMethodStats.latency = unfiltedMethodStats.latency.filter(
            getFilter(settings)
          );
          render();
        }).finally(() => loadDataIsPending = false);
      }).catch(() => loadDataIsPending = false);
    }
  };

  render = function () {
    if (routeIndex != thisRouteIndex) return;
    renderPage(
      <Page title="Overview">
        <Overview
          dashboardCounters={dashboardCounters}
          clusterStats={clusterStats}
          grainMethodStats={grainMethodStats}
        />
      </Page>,
      '#/'
    );
  };

  events.on('dashboard-counters', render);
  events.on('refresh', loadData);
  loadDashboardCounters();
});

(routie as any)('/grains', function () {
  const thisRouteIndex = ++routeIndex;
  events.clearAll();
  scroll();
  renderLoading();

  render = function () {
    if (routeIndex != thisRouteIndex) return;
    renderPage(
      <Page title="Grains">
        <Grains dashboardCounters={dashboardCounters} />
      </Page>,
      '#/grains'
    );
  };

  events.on('dashboard-counters', render);
  events.on('refresh', render);

  loadDashboardCounters();
});

(routie as any)('/silos', function () {
  const thisRouteIndex = ++routeIndex;
  events.clearAll();
  scroll();
  renderLoading();

  render = function () {
    if (routeIndex != thisRouteIndex) return;
    renderPage(
      <Page title="Silos">
        <Silos dashboardCounters={dashboardCounters} />
      </Page>,
      '#/silos'
    );
  };

  events.on('dashboard-counters', render);
  events.on('refresh', render);

  loadDashboardCounters();
});

(routie as any)('/host/:host', function (host: string) {
  const thisRouteIndex = ++routeIndex;
  events.clearAll();
  scroll();
  renderLoading();

  let siloProperties: any = {};

  let siloData: any[] = [];
  let siloStats: any[] = [];
  const loadData = function (cb?: any) {
    http.get(`HistoricalStats/${host}`, (err, data) => {
      siloData = data;
      render();
    });
    http.get(`SiloStats/${host}`, (err, data) => {
      siloStats = data;
      render();
    });
  };

  const renderOverloaded = function () {
    if (!siloData.length) return null;
    if (!siloData[siloData.length - 1]) return null;
    if (!siloData[siloData.length - 1].isOverloaded) return null;
    return (
      <small>
        <span className="label label-danger">OVERLOADED</span>
      </small>
    );
  };

  render = function () {
    if (routeIndex != thisRouteIndex) return;
    const silo =
      (dashboardCounters.hosts || []).filter(
        (x: any) => x.siloAddress === host
      )[0] || {};
    const subTitle = (
      <span>
        <SiloState status={silo.status} /> {renderOverloaded()}
      </span>
    );
    renderPage(
      <Page title={`Silo ${host}`} subTitle={subTitle}>
        <Silo
          silo={host}
          data={siloData}
          siloProperties={siloProperties}
          dashboardCounters={dashboardCounters}
          siloStats={siloStats}
        />
      </Page>,
      '#/silos'
    );
  };

  events.on('dashboard-counters', render);
  events.on('refresh', loadData);

  http.get('SiloProperties/' + host, function (err, data) {
    siloProperties = data;
    loadData();
  });
});

(routie as any)('/host/:host/counters', function (host: string) {
  const thisRouteIndex = ++routeIndex;
  events.clearAll();
  scroll();
  renderLoading();

  http.get(`SiloCounters/${host}`, (err, data) => {
    if (routeIndex != thisRouteIndex) return;
    const subTitle = <a href={`#/host/${host}`}>Silo Details</a>;
    renderPage(
      <Page title={`Silo ${host}`} subTitle={subTitle}>
        <SiloCounters
          silo={host}
          dashboardCounters={dashboardCounters}
          counters={data}
        />
      </Page>,
      '#/silos'
    );
  });
});

(routie as any)('/grain/:grainType', function (grainType: string) {
  const thisRouteIndex = ++routeIndex;
  events.clearAll();
  scroll();
  renderLoading();

  let grainStats: any = {};
  let loadDataIsPending = false;
  const loadData = function (cb?: any) {
    if (!loadDataIsPending) {
      http.get('GrainStats/' + grainType, function (err, data) {
        grainStats = data;
        render();
      }).finally(() => loadDataIsPending = false);
    }
  };

  render = function () {
    if (routeIndex != thisRouteIndex) return;
    renderPage(
      <Grain
        grainType={grainType}
        dashboardCounters={dashboardCounters}
        grainStats={grainStats}
      />,
      '#/grains'
    );
  };

  events.on('dashboard-counters', render);
  events.on('refresh', loadData);

  loadData();
});

(routie as any)('/grainDetails', function () {
  const thisRouteIndex = ++routeIndex;
  events.clearAll();
  scroll();
  renderLoading();

  let grainTypes: any = {};
  let loadDataIsPending = false;
  const loadData = function (cb?: any) {
    if (!loadDataIsPending) {
      http.get('GrainTypes', function (err, data) {
        grainTypes = data;
        render();
      }).finally(() => loadDataIsPending = false);
    }
  };

  render = function () {
    if (routeIndex != thisRouteIndex) return;
    renderPage(
      <GrainDetails
        grainTypes={grainTypes}
      />,
      '#/grainState'
    );
  };

  loadData();
});

(routie as any)('/reminders/:page?', function (page?: string) {
  const thisRouteIndex = ++routeIndex;
  events.clearAll();
  scroll();
  renderLoading();

  let remindersData: any[] = [];
  let pageNum: number;
  if (page) {
    pageNum = parseInt(page);
  } else {
    pageNum = 1;
  }

  const renderReminders = function () {
    if (routeIndex != thisRouteIndex) return;
    renderPage(
      <Page title="Reminders">
        <Reminders remindersData={remindersData} page={pageNum} />
      </Page>,
      '#/reminders'
    );
  };

  const rerouteToLastPage = function (lastPage: number) {
    return (document.location.hash = `/reminders/${lastPage}`);
  };

  let loadDataIsPending = false;
  const loadData = function (cb?: any) {
    if (!loadDataIsPending) {
      loadDataIsPending = true;
      http.get(`Reminders/${pageNum}`, function (err, data) {
        remindersData = data;
        renderReminders();
      }).finally(() => loadDataIsPending = false);
    }
  };

  events.on('long-refresh', loadData);

  loadData();
});

(routie as any)('/trace', function () {
  const thisRouteIndex = ++routeIndex;
  events.clearAll();
  scroll();
  const xhr = http.stream('Trace');
  renderPage(<LogStream xhr={xhr} />, '#/trace');
});

(routie as any)('/preferences', function () {
  const thisRouteIndex = ++routeIndex;
  events.clearAll();
  scroll();
  renderLoading();

  const changeSettings = (newSettings: Partial<Settings>) => {
    settings = {
      ...settings
    };

    if (newSettings.hasOwnProperty('dashboardGrainsHidden')) {
      storage.put(
        'dashboardGrains',
        newSettings.dashboardGrainsHidden ? 'hidden' : 'visible'
      );
      settings.dashboardGrainsHidden = newSettings.dashboardGrainsHidden!;
    }

    if (newSettings.hasOwnProperty('systemGrainsHidden')) {
      storage.put(
        'systemGrains',
        newSettings.systemGrainsHidden ? 'hidden' : 'visible'
      );
      settings.systemGrainsHidden = newSettings.systemGrainsHidden!;
    }

    dashboardCounters.simpleGrainStats = unfilteredDashboardCounters.simpleGrainStats.filter(
      getFilter(settings)
    );
    events.emit('dashboard-counters', dashboardCounters);
  };

  render = function () {
    if (routeIndex != thisRouteIndex) return;
    renderPage(
      <Page title="Preferences">
        <Preferences
          changeSettings={changeSettings}
          settings={settings}
          defaultTheme={defaultTheme}
          light={light}
          dark={dark}
        />
      </Page>,
      '#/preferences'
    );
  };
  loadDashboardCounters();

  render();
});

setInterval(() => events.emit('refresh'), 1000);
setInterval(() => events.emit('long-refresh'), 10000);

(routie as any).reload();
getVersion();

function getMenu(): MenuItem[] {
  const result: MenuItem[] = [
    {
      name: 'Overview',
      path: '#/',
      icon: 'fa fa-tachometer-alt'
    },
    {
      name: 'Grains',
      path: '#/grains',
      icon: 'fa fa-cubes'
    },
    {
      name: 'Grain Details',
      path: '#/grainDetails',
      icon: 'fa fa-cube'
    },
    {
      name: 'Silos',
      path: '#/silos',
      icon: 'fa fa-database'
    },
    {
      name: 'Reminders',
      path: '#/reminders',
      icon: 'fa fa-calendar'
    }
  ];

  if (!(window as any).hideTrace) {
    result.push({
      name: 'Log Stream',
      path: '#/trace',
      icon: 'fa fa-bars'
    });
  }

  result.push({
    name: 'Preferences',
    path: '#/preferences',
    icon: 'fa fa-cog',
    isSeparated: true
  });

  return result;
}

function getFilter(settings: Settings): (x: GrainStatItem) => boolean {
  let filter: (x: GrainStatItem) => boolean;
  if (settings.dashboardGrainsHidden && settings.systemGrainsHidden) {
    filter = filterByBothDashSys;
  } else if (settings.dashboardGrainsHidden) {
    filter = filterByDashboard;
  } else if (settings.systemGrainsHidden) {
    filter = filterBySystem;
  } else {
    filter = () => true;
  }
  return filter;
}

function filterByDashboard(x: GrainStatItem): boolean {
  if (x.grainType == undefined) {
    const dashboardGrain = x.grain!.startsWith('OrleansDashboard.');
    return !dashboardGrain;
  } else {
    const dashboardGrain = x.grainType.startsWith('OrleansDashboard.');
    return !dashboardGrain;
  }
}

function filterBySystem(x: GrainStatItem): boolean {
  if (x.grainType == undefined) {
    const systemGrain = x.grain!.startsWith('Orleans.');
    return !systemGrain;
  } else {
    const systemGrain = x.grainType.startsWith('Orleans.');
    return !systemGrain;
  }
}

function filterByBothDashSys(x: GrainStatItem): boolean {
  if (x.grainType == undefined) {
    const systemGrain = x.grain!.startsWith('Orleans.');
    const dashboardGrain = x.grain!.startsWith('OrleansDashboard.');
    return !systemGrain && !dashboardGrain;
  } else {
    const systemGrain = x.grainType.startsWith('Orleans.');
    const dashboardGrain = x.grainType.startsWith('OrleansDashboard.');
    return !systemGrain && !dashboardGrain;
  }
}

function light() {
  // Save preference to localStorage.
  storage.put('theme', 'light');
  defaultTheme = 'light';

  // Remove dark mode class from body and set Bootstrap theme to light.
  const body = document.getElementById('body')!;
  body.classList.remove('dark-mode');
  body.setAttribute('data-bs-theme', 'light');
}

function dark() {
  // Save preference to localStorage.
  storage.put('theme', 'dark');
  defaultTheme = 'dark';

  // Add dark mode class to body and set Bootstrap theme to dark.
  const body = document.getElementById('body')!;
  body.classList.add('dark-mode');
  body.setAttribute('data-bs-theme', 'dark');
}
