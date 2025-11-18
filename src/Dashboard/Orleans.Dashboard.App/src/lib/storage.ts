// polyfill localStorage with temp store
interface StorageInterface {
  [key: string]: any;
  removeItem?: (key: string) => void;
}

let store: StorageInterface = {};

try {
  if (typeof localStorage !== 'undefined') {
    store = localStorage;
  }
} catch (e) {
  // noop
}

const storageModule = {
  put: (key: string, value: string): void => {
    store[key] = value;
  },

  get: (key: string): string | undefined => {
    return store[key];
  },

  del: (key: string): void => {
    if (store.removeItem) {
      store.removeItem(key);
    } else {
      delete store[key];
    }
  }
};

export default storageModule;
