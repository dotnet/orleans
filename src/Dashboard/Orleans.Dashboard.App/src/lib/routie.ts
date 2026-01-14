interface RouteKey {
  name: string;
  optional: boolean;
}

interface RouteMap {
  [key: string]: Route;
}

interface NavigateOptions {
  silent?: boolean;
}

let routes: Route[] = [];
let map: RouteMap = {};
const reference = 'routie';
const oldReference = (window as any)[reference];

class Route {
  name: string | null;
  path: string;
  keys: RouteKey[];
  fns: Function[];
  params: { [key: string]: string };
  regex: RegExp;

  constructor(path: string, name: string | null) {
    this.name = name;
    this.path = path;
    this.keys = [];
    this.fns = [];
    this.params = {};
    this.regex = pathToRegexp(this.path, this.keys, false, false);
  }

  addHandler(fn: Function): void {
    this.fns.push(fn);
  }

  removeHandler(fn: Function): void {
    for (let i = 0, c = this.fns.length; i < c; i++) {
      const f = this.fns[i];
      if (fn == f) {
        this.fns.splice(i, 1);
        return;
      }
    }
  }

  run(params: any[]): void {
    for (let i = 0, c = this.fns.length; i < c; i++) {
      this.fns[i].apply(this, params);
    }
  }

  match(path: string, params: any[]): boolean {
    const m = this.regex.exec(path);

    if (!m) return false;

    for (let i = 1, len = m.length; i < len; ++i) {
      const key = this.keys[i - 1];

      const val = typeof m[i] === 'string' ? decodeURIComponent(m[i]) : m[i];

      if (key) {
        this.params[key.name] = val;
      }
      params.push(val);
    }

    return true;
  }

  toURL(params: { [key: string]: string }): string {
    let path = this.path;
    for (const param in params) {
      path = path.replace('/:' + param, '/' + params[param]);
    }
    path = path.replace(/\/:.*\?/g, '/').replace(/\?/g, '');
    if (path.indexOf(':') != -1) {
      throw new Error('missing parameters for url: ' + path);
    }
    return path;
  }
}

const pathToRegexp = function(
  path: string | RegExp | string[],
  keys: RouteKey[],
  sensitive: boolean,
  strict: boolean
): RegExp {
  if (path instanceof RegExp) return path;
  if (path instanceof Array) path = '(' + path.join('|') + ')';
  path = (path as string)
    .concat(strict ? '' : '/?')
    .replace(/\/\(/g, '(?:/')
    .replace(/\+/g, '__plus__')
    .replace(/(\/)?(\.)?:(\w+)(?:(\(.*?\)))?(\?)?/g, function(
      _: string,
      slash: string,
      format: string,
      key: string,
      capture: string,
      optional: string
    ) {
      keys.push({ name: key, optional: !!optional });
      slash = slash || '';
      return (
        '' +
        (optional ? '' : slash) +
        '(?:' +
        (optional ? slash : '') +
        (format || '') +
        (capture || ((format && '([^/.]+?)') || '([^/]+?)')) +
        ')' +
        (optional || '')
      );
    })
    .replace(/([\/.])/g, '\\$1')
    .replace(/__plus__/g, '(.+)')
    .replace(/\*/g, '(.*)');
  return new RegExp('^' + path + '$', sensitive ? '' : 'i');
};

const addHandler = function(path: string, fn: Function): void {
  const s = path.split(' ');
  const name = s.length == 2 ? s[0] : null;
  path = s.length == 2 ? s[1] : s[0];

  if (!map[path]) {
    map[path] = new Route(path, name);
    routes.push(map[path]);
  }
  map[path].addHandler(fn);
};

interface RoutieFunction {
  (path: string, fn: Function): void;
  (path: { [key: string]: Function }): void;
  (path: string): void;
  lookup: (name: string, obj: { [key: string]: string }) => string | undefined;
  remove: (path: string, fn: Function) => void;
  removeAll: () => void;
  navigate: (path: string, options?: NavigateOptions) => void;
  noConflict: () => RoutieFunction;
  reload: () => void;
}

const routie = function(path: string | { [key: string]: Function }, fn?: Function): void {
  if (typeof fn == 'function') {
    addHandler(path as string, fn);
  } else if (typeof path == 'object') {
    for (const p in path) {
      addHandler(p, path[p]);
    }
  } else if (typeof fn === 'undefined') {
    routie.navigate(path as string);
  }
} as RoutieFunction;

routie.lookup = function(name: string, obj: { [key: string]: string }): string | undefined {
  for (let i = 0, c = routes.length; i < c; i++) {
    const route = routes[i];
    if (route.name == name) {
      return route.toURL(obj);
    }
  }
};

routie.remove = function(path: string, fn: Function): void {
  const route = map[path];
  if (!route) return;
  route.removeHandler(fn);
};

routie.removeAll = function(): void {
  map = {};
  routes = [];
};

routie.navigate = function(path: string, options?: NavigateOptions): void {
  options = options || {};
  const silent = options.silent || false;

  if (silent) {
    removeListener();
  }
  setTimeout(function() {
    window.location.hash = path;

    if (silent) {
      setTimeout(function() {
        addListener();
      }, 1);
    }
  }, 1);
};

routie.noConflict = function(): RoutieFunction {
  (window as any)[reference] = oldReference;
  return routie;
};

const getHash = function(): string {
  return window.location.hash.substring(1);
};

const checkRoute = function(hash: string, route: Route): boolean {
  const params: any[] = [];
  if (route.match(hash, params)) {
    route.run(params);
    return true;
  }
  return false;
};

const hashChanged = (routie.reload = function(): void {
  const hash = getHash();
  for (let i = 0, c = routes.length; i < c; i++) {
    const route = routes[i];
    if (checkRoute(hash, route)) {
      return;
    }
  }
});

const addListener = function(): void {
  if (window.addEventListener) {
    window.addEventListener('hashchange', hashChanged, false);
  } else {
    (window as any).attachEvent('onhashchange', hashChanged);
  }
};

const removeListener = function(): void {
  if (window.removeEventListener) {
    window.removeEventListener('hashchange', hashChanged);
  } else {
    (window as any).detachEvent('onhashchange', hashChanged);
  }
};

addListener();

export default routie;
