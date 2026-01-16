# Ensure UTF-8 throughout the pipeline
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding = [System.Text.Encoding]::UTF8
$env:PYTHONIOENCODING = "utf-8"

# Set console codepage to UTF-8 (suppressing output)
chcp 65001 | Out-Null

# Read prompt from file and run opencode with JSON output
# Note: Configure permissions in opencode.json (e.g., "permission": { "edit": "allow", "bash": "allow" })
Get-Content .opencode/.ralph/prompt.md -Encoding UTF8 |
    opencode run --command implement-feature --format json |
    Tee-Object -FilePath .opencode/.ralph/opencode_output.jsonl -Append |
    uvx --from rich python .opencode/.ralph/visualize.py --debug