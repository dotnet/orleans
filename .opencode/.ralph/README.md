# Ralph Wiggum Method: Autonomous Execution

Run AI agents in continuous loops until task completion - no manual intervention required.

> **Note:** Currently only supported for OpenCode. For Claude Code users, see `.claude/.ralph/README.md`.

**How it works:** Agent reads `.opencode/.ralph/prompt.md`, executes tasks, iterates until done, manages its own context.

## Platform Support

Ralph supports both Mac/Linux (bash) and Windows (PowerShell):

| Platform  | Scripts Location    | Usage                   |
| --------- | ------------------- | ----------------------- |
| Mac/Linux | `.opencode/.ralph/` | `ralph.sh`, `sync.sh`   |
| Windows   | `.opencode/.ralph/` | `ralph.ps1`, `sync.ps1` |

## 1 Minute Quick Start

### Prerequisites: Have Your Feature List Ready

**Ralph loops through your `research/feature-list.json` and implements features autonomously.** Before using Ralph, you must complete the research and planning phases to generate your feature list.

If you don't have a `feature-list.json` yet, follow the [Procedure section in the main README](../../README.md#our-procedure-follow-step-by-step-or-use-commands-and-sub-agents-in-repo-to-build-your-own):

Once you have your approved spec and `feature-list.json`, continue below.

### Quick Start Steps

1. **Update `.opencode/.ralph/prompt.md`** with specific instructions after the `/implement-feature` slash command for what you want to implement. Keep it concise

2. **Test one iteration:**

   **Mac/Linux:**
   ```bash
   cd /path/to/your-project
   ./.opencode/.ralph/sync.sh
   ```

   **Windows PowerShell:**
   ```powershell
   cd C:/path/to/your-project
   ./.opencode/.ralph/sync.ps1
   ```
   Verifies the agent can read your prompt and execute successfully

3. **Run continuously:**

   **Mac/Linux:**
   ```bash
   ./.opencode/.ralph/ralph.sh
   ```

   **Windows PowerShell:**
   ```powershell
   ./.opencode/.ralph/ralph.ps1
   ```
   Agent loops, working until task completion

## Controlling Iterations

By default, Ralph runs indefinitely until the task is complete. You can limit the number of iterations using the `max_iterations` parameter to define your own "done" criteria:

**Mac/Linux:**
```bash
# Run exactly 10 iterations
./.opencode/.ralph/ralph.sh -m 10

# Run indefinitely (default)
./.opencode/.ralph/ralph.sh
```

**Windows PowerShell:**
```powershell
# Run exactly 10 iterations
./.opencode/.ralph/ralph.ps1 -MaxIterations 10

# Run indefinitely (default)
./.opencode/.ralph/ralph.ps1
```

This is useful, depending on your use case, for:
- Budget control (limit API calls)
- Testing a fixed amount of work
- Running overnight with a cap
- Defining completion based on iteration count rather than only relying on agent judgment

## Best Environments to Run Ralph

Since Ralph runs continuously, it's best to run it in environments designed for long-running processes. Consider the following options:
- **Cloud VM**: Use a terminal multiplexer like [tmux](https://github.com/tmux/tmux) and setup your development environment with basic tools (git, Node.js, Python, Rust, C, C++, etc.)
  - Providers: AWS EC2, Google Cloud Compute Engine, DigitalOcean Droplets, Kamatera, etc. NOTE: You can start with a more cost-effective option like DigitalOcean or Kamatera for pay-as-you-go or $4-6/month. If your organization already has AWS, GCP, etc. feel free to leverage those.

## Agent Prompt Guidelines

### Best Practices

**Keep prompts short and concise.** Effective agent prompts are clear and focused, not verbose. Detailed specifications should be maintained in separate documents (specs, design docs, etc.) and referenced when needed.

**Additional guidelines:**
- One task per loop
- Clear completion criteria
