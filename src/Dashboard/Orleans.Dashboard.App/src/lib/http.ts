import events from 'eventthing';

type HttpCallback = (error: string | null, result?: any) => void;

function makeRequest(method: string, uri: string, body: string | null, cb: HttpCallback): Promise<any> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open(method, uri, true);
    xhr.onreadystatechange = function() {
      if (xhr.readyState !== 4) return;
      if (xhr.status < 400 && xhr.status > 0) {
        const result = JSON.parse(xhr.responseText || '{}');
        resolve(result);
        return cb(null, result);
      }
      const errorMessage =
        'Error connecting to Orleans Silo. Status code: ' +
        (xhr.status || 'NO_CONNECTION');
      errorHandlers.forEach(x => x(errorMessage));
      reject(errorMessage);
    };
    xhr.setRequestHeader('Content-Type', 'application/json');
    xhr.setRequestHeader('Accept', 'application/json');
    xhr.send(body);
  });
}

type ErrorHandler = (error: string) => void;

const errorHandlers: ErrorHandler[] = [];

const httpModule = {
  get: function(url: string, cb: HttpCallback): Promise<any> {
    return makeRequest('GET', url, null, cb);
  },

  stream: function(url: string): XMLHttpRequest {
    const xhr = new XMLHttpRequest();
    xhr.open('GET', url, true);
    xhr.send();
    return xhr;
  },

  onError: function(handler: ErrorHandler): void {
    errorHandlers.push(handler);
  }
};

export default httpModule;
