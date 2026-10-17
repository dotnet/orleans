// durable-operations.js: starts and observes durable HTTP operations from the browser, idempotently.
//
// Each user intent, such as checking out a basket, gets an idempotency key which the helper persists before it sends
// the request. Retries of the request, after a network failure, a double click, or a page refresh, carry the same key,
// so the server reaches the operation which the first attempt started. Once the server acknowledges the start, the
// helper forgets the intent's key and persists the operation's Operation-Location until the operation is terminal, so a
// refreshed page resumes observing it.
//
// The helper speaks the protocol of Orleans durable operations, which is compatible with Azure long-running operations:
// a start returns 200 OK with the result, or 202 Accepted or 424 Failed Dependency with a status monitor at
// Operation-Location; the monitor reports { id, status, result, error }; {monitor}/events streams the status as
// server-sent events; GET {monitor} with Prefer: wait=N long-polls; POST {monitor}:cancel cancels; and
// POST {monitor}/signals/{name} delivers a signal.

const terminalStatuses = new Set(["Succeeded", "Failed", "Canceled"]);

/** Returns whether a status monitor's status is terminal. */
export const isTerminal = operation => terminalStatuses.has(operation?.status);

/** Reports a response which neither completes nor locates an operation. */
export class OperationError extends Error {
  constructor(response, problem) {
    super(problem?.detail ?? problem?.title ?? `${response.status} ${response.statusText}`);
    this.name = "OperationError";
    this.status = response.status;
    this.problem = problem;
  }
}

export class DurableOperations {
  /**
   * @param {object} [options]
   * @param {Storage} [options.storage] Persists intents and running operations; sessionStorage by default, which
   *   survives refreshes of the tab.
   * @param {string} [options.prefix] Prefixes the storage keys.
   * @param {number[]} [options.retryDelays] Delays, in milliseconds, before each retry of a request which fails with a
   *   network error.
   * @param {number} [options.wait] Seconds which a long poll asks the server to wait.
   */
  constructor({ storage = sessionStorage, prefix = "durable-operations", retryDelays = [250, 1000, 3000], wait = 30 } = {}) {
    this.storage = storage;
    this.prefix = prefix;
    this.retryDelays = retryDelays;
    this.wait = wait;
  }

  /** Returns the persisted key of an intent, creating one for a new intent. */
  key(intent) {
    const record = this.#read(`intent:${intent}`) ?? { key: crypto.randomUUID() };
    this.#write(`intent:${intent}`, record);
    return record.key;
  }

  /** Forgets an intent, so its next start gets a new key. */
  forget(intent) {
    this.storage.removeItem(this.#name(`intent:${intent}`));
  }

  /** Returns the starts which the server hasn't acknowledged: [{ intent, key, request }]. */
  pending() {
    return this.#entries("intent:")
      .filter(([, record]) => record.request)
      .map(([intent, record]) => ({ intent, ...record }));
  }

  /** Returns the running operations which the helper observes: [{ location, intent, data }]. */
  watching() {
    return Object.entries(this.#read("watching") ?? {}).map(([location, entry]) => ({ location, ...entry }));
  }

  /**
   * Starts the operation of an intent, or reaches the operation which an earlier attempt of the intent started.
   *
   * @param {string} intent Names the user intent, such as `checkout:alice`.
   * @param {(key: string) => { url: string, method?: string, body?: unknown, data?: unknown }} build Builds the request
   *   from the intent's key. The helper persists the request, so a resumed start sends the same request.
   * @param {object} [options]
   * @param {(attempt: number) => Record<string, string>} [options.headers] Adds headers to an attempt.
   * @param {number} [options.wait] Seconds which the start asks the server to wait for the result.
   * @returns {Promise<{ key: string, location?: string, operation?: object, result?: unknown, data?: unknown }>}
   *   The operation's status monitor while it runs or once it failed, or its result once it succeeded.
   */
  async start(intent, build, { headers, wait } = {}) {
    const name = `intent:${intent}`;
    const record = this.#read(name) ?? { key: crypto.randomUUID() };
    record.request ??= build(record.key);
    this.#write(name, record);
    return this.#send(intent, record, { headers, wait });
  }

  /**
   * Sends the starts which the server hasn't acknowledged again, with their keys, such as after a refresh during a
   * start. Returns the starts' outcomes, in the order of pending().
   */
  async resume() {
    return Promise.all(this.pending().map(({ intent, ...record }) => this.#send(intent, record, {})));
  }

  /**
   * Observes an operation until it is terminal, with its server-sent events, or with long polls where events are
   * unavailable, and forgets it once it is terminal or the server no longer holds it.
   *
   * @param {string} location The operation's status monitor.
   * @param {object} [options]
   * @param {(operation: object) => void} [options.onStatus] Receives each status.
   * @param {AbortSignal} [options.signal] Stops observing.
   * @returns {Promise<object>} The operation's terminal status monitor.
   * @throws {OperationError} With status 404 when the operation doesn't exist, or its retention has ended.
   */
  async observe(location, { onStatus, signal } = {}) {
    let operation = typeof EventSource === "undefined" ? undefined : await this.#observeEvents(location, onStatus, signal);
    try {
      while (!isTerminal(operation)) {
        signal?.throwIfAborted();
        operation = await this.get(location, { wait: this.wait, signal });
        onStatus?.(operation);
      }
    } catch (error) {
      if (error instanceof OperationError && error.status === 404) this.#unwatch(location);
      throw error;
    }

    this.#unwatch(location);
    return operation;
  }

  /** Reads an operation's status monitor, long-polling for wait seconds while it runs. */
  async get(location, { wait = 0, signal } = {}) {
    const response = await this.#fetch(location, { headers: wait > 0 ? { Prefer: `wait=${wait}` } : {}, signal });
    return this.#readOperation(response);
  }

  /** Requests cancellation of an operation, carrying a reason, and returns its status monitor. */
  async cancel(location, reason) {
    const response = await this.#fetch(`${location}:cancel`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reason }),
    });
    return this.#readOperation(response);
  }

  /**
   * Delivers a signal with a JSON value to an operation. A repeated signal with an equal value succeeds, so a retry is
   * safe; derive the value of an intent from key(intent), so a resumed intent sends the same value.
   */
  async signal(location, name, value) {
    const response = await this.#fetch(`${location}/signals/${encodeURIComponent(name)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(value),
    });
    if (!response.ok) throw new OperationError(response, await readProblem(response));
  }

  async #send(intent, record, { headers, wait }) {
    const { url, method = "POST", body, data } = record.request;
    const response = await this.#fetch(url, {
      method,
      headers: attempt => ({
        "Idempotency-Key": record.key,
        ...(body === undefined ? {} : { "Content-Type": "application/json" }),
        ...(wait > 0 ? { Prefer: `wait=${wait}` } : {}),
        ...headers?.(attempt),
      }),
      body: body === undefined ? undefined : JSON.stringify(body),
    });

    // The server answered, so it holds the start's outcome, and the intent's key has served its purpose.
    const location = response.headers.get("Operation-Location") ?? undefined;
    if (response.status === 202 || response.status === 424) {
      const operation = await response.json();
      this.forget(intent);
      if (!isTerminal(operation)) this.#watch(location, { intent, data });
      return { key: record.key, location, operation, data };
    }

    if (response.ok) {
      this.forget(intent);
      const result = response.status === 204 ? undefined : await response.json();
      return { key: record.key, location, result, data };
    }

    // A server error leaves the start unacknowledged, so a retry sends the same key; a client error is final.
    if (response.status < 500) this.forget(intent);
    throw new OperationError(response, await readProblem(response));
  }

  // Observes an operation's events until it is terminal. Returns the last status, or undefined, when the stream closes
  // before the terminal status, such as when the server doesn't serve events, so the caller long-polls.
  #observeEvents(location, onStatus, signal) {
    return new Promise(resolve => {
      const events = new EventSource(`${location}/events`);
      let last;
      const finish = () => {
        events.close();
        signal?.removeEventListener("abort", finish);
        resolve(last);
      };
      signal?.addEventListener("abort", finish, { once: true });
      events.addEventListener("status", event => {
        last = JSON.parse(event.data);
        onStatus?.(last);
        if (isTerminal(last)) finish();
      });

      // The browser reconnects an interrupted stream with Last-Event-ID; a stream which it closes ends the events.
      events.onerror = () => {
        if (events.readyState === EventSource.CLOSED) finish();
      };
    });
  }

  // Sends a request, retrying network failures with the configured delays. The headers may vary by attempt.
  async #fetch(url, { method = "GET", headers = {}, body, signal } = {}) {
    for (let attempt = 0; ; attempt++) {
      try {
        return await fetch(url, { method, headers: typeof headers === "function" ? headers(attempt) : headers, body, signal });
      } catch (error) {
        if (signal?.aborted || attempt >= this.retryDelays.length) throw error;
        await new Promise(resolve => setTimeout(resolve, this.retryDelays[attempt]));
      }
    }
  }

  async #readOperation(response) {
    if (!response.ok) throw new OperationError(response, await readProblem(response));
    return response.json();
  }

  #watch(location, entry) {
    if (!location) return;
    const watching = this.#read("watching") ?? {};
    watching[location] = entry;
    this.#write("watching", watching);
  }

  #unwatch(location) {
    const watching = this.#read("watching") ?? {};
    delete watching[location];
    this.#write("watching", watching);
  }

  #entries(kind) {
    const prefix = this.#name(kind);
    const entries = [];
    for (let i = 0; i < this.storage.length; i++) {
      const name = this.storage.key(i);
      if (name?.startsWith(prefix)) entries.push([name.slice(prefix.length), JSON.parse(this.storage.getItem(name))]);
    }

    return entries;
  }

  #name(name) {
    return `${this.prefix}:${name}`;
  }

  #read(name) {
    const value = this.storage.getItem(this.#name(name));
    return value === null ? undefined : JSON.parse(value);
  }

  #write(name, value) {
    this.storage.setItem(this.#name(name), JSON.stringify(value));
  }
}

async function readProblem(response) {
  try {
    return response.headers.get("content-type")?.includes("json") ? await response.json() : { detail: (await response.text()) || undefined };
  } catch {
    return undefined;
  }
}
