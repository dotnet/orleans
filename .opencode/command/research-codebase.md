---
description: Document codebase as-is with research directory for historical context.
agent: build
model: anthropic/claude-opus-4-5
---

# Research Codebase

You are tasked with conducting comprehensive research across the codebase to answer user questions by spawning parallel sub-agents and synthesizing their findings.

The user's research question/request is: **$ARGUMENTS**

## Steps to follow after receiving the research query:

IMPORTANT: OPTIMIZE the user's research question request using your prompt-engineer skill and confirm that the your refined question captures the user's intent BEFORE proceeding.

1. **Read any directly mentioned files first:**
   - If the user mentions specific files (tickets, docs, or other notes), read them FULLY first
   - **IMPORTANT**: Use the `readFile` tool WITHOUT limit/offset parameters to read entire files
   - **CRITICAL**: Read these files yourself in the main context before spawning any sub-tasks
   - This ensures you have full context before decomposing the research

2. **Analyze and decompose the research question:**
   - Break down the user's query into composable research areas
   - Take time to ultrathink about the underlying patterns, connections, and architectural implications the user might be seeking
   - Identify specific components, patterns, or concepts to investigate
   - Create a research plan using TodoWrite to track all subtasks
   - Consider which directories, files, or architectural patterns are relevant

3. **Spawn parallel sub-agent tasks for comprehensive research:**
   - Create multiple Task agents to research different aspects concurrently
   - We now have specialized agents that know how to do specific research tasks:

   **For codebase research:**
   - Use the **codebase-locator** agent to find WHERE files and components live
   - Use the **codebase-analyzer** agent to understand HOW specific code works (without critiquing it)
   - Use the **codebase-pattern-finder** agent to find examples of existing patterns (without evaluating them)
   - Output directory: `research/docs/`
   - Examples:
     - The database logic is found and can be documented in `research/docs/2024-01-10-database-implementation.md`
     - The authentication flow is found and can be documented in `research/docs/2024-01-11-authentication-flow.md`

   **IMPORTANT**: All agents are documentarians, not critics. They will describe what exists without suggesting improvements or identifying issues.

   **For research directory:**
   - Use the **codebase-research-locator** agent to discover what documents exist about the topic
   - Use the **codebase-research-analyzer** agent to extract key insights from specific documents (only the most relevant ones)

   **For online search:**
   - VERY IMPORTANT: In case you discover external libraries as dependencies, use the **codebase-online-researcher** agent for external documentation and resources
     - If you use DeepWiki tools, instruct the agent to return references to code snippets or documentation, PLEASE INCLUDE those references (e.g. source file names, line numbers, etc.)
     - If you perform a web search using the webfetch tool, instruct the agent to return LINKS with their findings, and please INCLUDE those links in the research document
     - Output directory: `research/docs/`
     - Examples:
       - If researching `Redis` locks usage, the agent might find relevant usage and create a document `research/docs/2024-01-15-redis-locks-usage.md` with internal links to Redis docs and code references
       - If researching `OAuth` flows, the agent might find relevant external articles and create a document `research/docs/2024-01-16-oauth-flows.md` with links to those articles

   The key is to use these agents intelligently:
   - Start with locator agents to find what exists
   - Then use analyzer agents on the most promising findings to document how they work
   - Run multiple agents in parallel when they're searching for different things
   - Each agent knows its job - just tell it what you're looking for
   - Don't write detailed prompts about HOW to search - the agents already know
   - Remind agents they are documenting, not evaluating or improving

4. **Wait for all sub-agents to complete and synthesize findings:**
   - IMPORTANT: Wait for ALL sub-agent tasks to complete before proceeding
   - Compile all sub-agent results (both codebase and research findings)
   - Prioritize live codebase findings as primary source of truth
   - Use research findings as supplementary historical context
   - Connect findings across different components
   - Include specific file paths and line numbers for reference
   - Highlight patterns, connections, and architectural decisions
   - Answer the user's specific questions with concrete evidence

5. **Generate research document:**

   - Follow the directory structure for research documents:
```
research/
├── tickets/
│   ├── YYYY-MM-DD-XXXX-description.md
├── docs/
│   ├── YYYY-MM-DD-topic.md
├── notes/
│   ├── YYYY-MM-DD-meeting.md
├── ...
└──
```
   - Naming conventions:
      - YYYY-MM-DD is today's date
      - topic is a brief kebab-case description of the research topic
      - meeting is a brief kebab-case description of the meeting topic
      - XXXX is the ticket number (omit if no ticket)
      - description is a brief kebab-case description of the research topic
      - Examples:
        - With ticket: `2025-01-08-1478-parent-child-tracking.md`
        - Without ticket: `2025-01-08-authentication-flow.md`
   - Structure the document with YAML frontmatter followed by content:
     ```markdown
     ---
     date: !`date '+%Y-%m-%d %H:%M:%S %Z'`
     researcher: [Researcher name from thoughts status]
     git_commit: !`git rev-parse HEAD`
     branch: !`git branch --show-current 2>/dev/null || git rev-parse --abbrev-ref HEAD`
     repository: !`basename $(git rev-parse --show-toplevel)`
     topic: "[User's Question/Topic]"
     tags: [research, codebase, relevant-component-names]
     status: complete
     last_updated: !`date '+%Y-%m-%d'`
     last_updated_by: [Researcher name]
     ---

     # Research

     ## Research Question
     [Original user query]

     ## Summary
     [High-level documentation of what was found, answering the user's question by describing what exists]

     ## Detailed Findings

     ### [Component/Area 1]
     - Description of what exists ([file.ext:line](link))
     - How it connects to other components
     - Current implementation details (without evaluation)

     ### [Component/Area 2]
     ...

     ## Code References
     - `path/to/file.py:123` - Description of what's there
     - `another/file.ts:45-67` - Description of the code block

     ## Architecture Documentation
     [Current patterns, conventions, and design implementations found in the codebase]

     ## Historical Context (from research/)
     [Relevant insights from research/ directory with references]
     - `research/docs/YYYY-MM-DD-topic.md` - Information about module X
     - `research/notes/YYYY-MM-DD-meeting.md` - Past notes from internal engineering, customer, etc. discussions
     - ...

     ## Related Research
     [Links to other research documents in research/]

     ## Open Questions
     [Any areas that need further investigation]
     ```

1. **Add GitHub permalinks (if applicable):**
   - Check if on main branch or if commit is pushed: `git branch --show-current` and `git status`
   - If on main/master or pushed, generate GitHub permalinks:
     - Get repo info: `gh repo view --json owner,name`
     - Create permalinks: `https://github.com/{owner}/{repo}/blob/{commit}/{file}#L{line}`
   - Replace local file references with permalinks in the document

2. **Present findings:**
   - Present a concise summary of findings to the user
   - Include key file references for easy navigation
   - Ask if they have follow-up questions or need clarification

3.  **Handle follow-up questions:**
   - If the user has follow-up questions, append to the same research document
   - Update the frontmatter fields `last_updated` and `last_updated_by` to reflect the update
   - Add `last_updated_note: "Added follow-up research for [brief description]"` to frontmatter
   - Add a new section: `## Follow-up Research [timestamp]`
   - Spawn new sub-agents as needed for additional investigation
   - Continue updating the document and syncing

## Important notes:
- Always use parallel Task agents to maximize efficiency and minimize context usage
- Always run fresh codebase research - never rely solely on existing research documents
- The `research/` directory provides historical context to supplement live findings
- Focus on finding concrete file paths and line numbers for developer reference
- Research documents should be self-contained with all necessary context
- Each sub-agent prompt should be specific and focused on read-only documentation operations
- Document cross-component connections and how systems interact
- Include temporal context (when the research was conducted)
- Link to GitHub when possible for permanent references
- Keep the main agent focused on synthesis, not deep file reading
- Have sub-agents document examples and usage patterns as they exist
- Explore all of research/ directory, not just research subdirectory
- **CRITICAL**: You and all sub-agents are documentarians, not evaluators
- **REMEMBER**: Document what IS, not what SHOULD BE
- **NO RECOMMENDATIONS**: Only describe the current state of the codebase
- **File reading**: Always read mentioned files FULLY (no limit/offset) before spawning sub-tasks
- **Critical ordering**: Follow the numbered steps exactly
  - ALWAYS read mentioned files first before spawning sub-tasks (step 1)
  - ALWAYS wait for all sub-agents to complete before synthesizing (step 4)
  - ALWAYS gather metadata before writing the document (step 5 before step 6)
  - NEVER write the research document with placeholder values

- **Frontmatter consistency**:
  - Always include frontmatter at the beginning of research documents
  - Keep frontmatter fields consistent across all research documents
  - Update frontmatter when adding follow-up research
  - Use snake_case for multi-word field names (e.g., `last_updated`, `git_commit`)
  - Tags should be relevant to the research topic and components studied

## Final Output

- A collection of research files with comprehensive research findings, properly formatted and linked, ready for consumption to create detailed specifications or design documents.
- IMPORTANT: DO NOT generate any other artifacts or files OUTSIDE of the `research/` directory.