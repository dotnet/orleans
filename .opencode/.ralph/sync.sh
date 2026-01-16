#!/usr/bin/env bash

# Ensure UTF-8 throughout the pipeline
export PYTHONIOENCODING="utf-8"
export LANG="en_US.UTF-8"
export LC_ALL="en_US.UTF-8"

# Read prompt from file and run opencode with JSON output
# Note: Configure permissions in opencode.json (e.g., "permission": { "edit": "allow", "bash": "allow" })
cat .opencode/.ralph/prompt.md | \
    opencode run --command implement-feature --format json | \
    tee -a .opencode/.ralph/opencode_output.jsonl | \
    uvx --from rich python .opencode/.ralph/visualize.py --debug