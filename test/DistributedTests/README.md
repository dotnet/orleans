# Distributed Tests

## Running locally

### Install crank and crank-agent

```sh
dotnet tool install -g Microsoft.Crank.Controller --version 0.2.0-*
```

```sh
dotnet tool install -g Microsoft.Crank.Agent --version "0.2.0-*"
```

### Run crank agent

Do this in a separate terminal.

```sh
crank-agent --url http://*:5010
```

### Build Orleans

```sh
dotnet build -c Release
```

### Run crank scenario

Run this from the root of the repository (next to Orleans.slnx):

```sh
crank --config .\distributed-tests.yml --scenario ping --profile local
```

Note: scenarios can be found in `distributed-tests.yml`.
