# Advanced Prompting Patterns

This document covers sophisticated prompt engineering techniques for complex tasks requiring structured reasoning, long-form content, or multi-step processing.

## Chain of Thought (CoT) Prompting

### What is Chain of Thought?
Chain of thought prompting encourages Claude to break down complex problems systematically. Giving Claude space to think can dramatically improve its performance on research, analysis, and problem-solving tasks.

### Key Benefits
- **Accuracy**: Stepping through problems reduces errors, especially in math, logic, analysis, or generally complex tasks
- **Coherence**: Structured reasoning produces more organized responses
- **Debugging**: Observing Claude's thought process reveals unclear prompt areas

### When to Use CoT
Apply CoT for tasks that a human would need to think through, like:
- Complex math or logic problems
- Multi-step analysis
- Writing complex documents
- Decisions with many factors
- Planning specs

**Trade-off**: Increased output length may impact latency, so avoid using CoT for straightforward tasks.

### Three CoT Techniques (Least to Most Complex)

**1. Basic Prompt**
Include "Think step-by-step" in your request. Simple but lacks specific guidance.

**2. Guided Prompt**
Outline specific steps for Claude's reasoning process. Provides direction without structuring the output format, making answer extraction more difficult.

**3. Structured Prompt**
Use XML tags like `<thinking>` and `<answer>` to separate reasoning from final answers. This enables easy parsing of both thought process and conclusions.

Example:
```
Please analyze this problem and provide your reasoning.

Put your step-by-step thinking in <thinking> tags.
Put your final answer in <answer> tags.
```

### Critical Implementation Note
**"Always have Claude output its thinking. Without outputting its thought process, no thinking occurs!"** Visible reasoning is essential for CoT effectiveness.

---

## Multishot Prompting

### Core Concept
Multishot prompting (also called few-shot prompting) involves providing a few well-crafted examples in your prompt to improve Claude's output quality. This technique is particularly effective for tasks requiring structured outputs or adherence to specific formats.

### Key Benefits
- **Accuracy**: Examples reduce misinterpretation of instructions
- **Consistency**: Examples enforce uniform structure and style
- **Performance**: Well-chosen examples boost Claude's ability to handle complex tasks

### Crafting Effective Examples

Examples should be:
1. **Relevant** — Mirror your actual use case
2. **Diverse** — Cover edge cases and vary sufficiently to avoid unintended pattern recognition
3. **Clear** — Wrapped in `<example>` tags (multiple examples nested in `<examples>` tags)

### Optimal Quantity
Include 3-5 diverse, relevant examples. More examples = better performance, especially for complex tasks.

### Template Structure
```xml
<examples>
  <example>
    <input>Sample input 1</input>
    <output>Expected output 1</output>
  </example>

  <example>
    <input>Sample input 2</input>
    <output>Expected output 2</output>
  </example>

  <example>
    <input>Sample input 3</input>
    <output>Expected output 3</output>
  </example>
</examples>
```

---

## Prompt Chaining

### Core Concept
Prompt chaining breaks complex tasks into smaller, sequential subtasks, with each step receiving Claude's focused attention. This approach improves accuracy, clarity, and traceability compared to handling everything in a single prompt.

### Key Benefits
1. **Accuracy**: Each subtask gets full attention, reducing errors
2. **Clarity**: Simpler instructions produce clearer outputs
3. **Traceability**: Issues can be pinpointed and fixed in specific steps

### When to Use Chaining
Apply this technique for multi-step tasks involving:
- Research synthesis and document analysis
- Iterative content creation
- Multiple transformations or citations
- Tasks where Claude might miss or mishandle steps

### Core Techniques

**1. Identify Subtasks**
Break work into distinct, sequential steps with single, clear objectives.

**2. Structure with XML**
Use XML tags to pass outputs between prompts for clear handoffs between steps.

**3. Single-Task Goals**
Each subtask should focus on one objective to maintain clarity.

**4. Iterate & Refine**
Adjust subtasks based on Claude's performance.

### Workflow Examples
- **Content pipelines**: Research → Outline → Draft → Edit → Format
- **Data processing**: Extract → Transform → Analyze → Visualize
- **Decision-making**: Gather info → List options → Analyze → Recommend
- **Verification loops**: Generate → Review → Refine → Re-review
- **Writing Specs**: Research → Plan → Implement (see detailed example below)

### Complex Example: Spec Workflow

This workflow represents a research-driven, AI-augmented software development process that emphasizes thorough planning and human oversight before implementation. It's designed to maximize quality and alignment by incorporating both AI assistance and human feedback at critical decision points.

**Phase 1: Research & Requirements**

1. **Deep Research** — Begin with comprehensive research into the problem space: understanding user needs, exploring existing solutions, reviewing relevant technologies, and identifying constraints. Build a solid foundation of knowledge before defining what to build.

2. **Product Requirements Document (PRD)** — Distill research findings into a formal PRD that articulates the *what* and *why*. Define the problem statement, target users, success metrics, user stories, and business objectives. Remain technology-agnostic, focusing purely on outcomes rather than implementation details.

**Phase 2: AI-Assisted Design**

3. **Brainstorm with Coding Agent** — This is where the workflow diverges from traditional approaches. Engineers collaborate with an AI coding agent to explore technical possibilities. This brainstorming session generates multiple implementation approaches, identifies potential challenges, discusses trade-offs, and leverages AI's knowledge of patterns and best practices. It's an exploratory phase that surfaces ideas that might not emerge from human-only brainstorming.

4. **Technical Design/Spec** — Formalize the brainstorming output into a technical specification describing the *how*: architecture decisions, API designs, data models, technology stack choices, system components and their interactions, scalability considerations, and security/performance requirements. This becomes the engineering blueprint for implementation.

**Phase 3: Human Validation Loop**

5. **Human Feedback** — A critical checkpoint where experienced engineers, architects, or technical leads review the spec. This human oversight ensures the AI-assisted design is sound, catches edge cases or concerns, validates assumptions, and aligns the technical approach with organizational standards and long-term architecture. This phase acknowledges that AI assistance needs human verification.

6. **Refined Technical Design/Spec** — Incorporate feedback to improve the specification. This might involve adjusting the architecture, adding clarifications, addressing edge cases, or reconsidering technology choices. The refined spec represents the agreed-upon technical approach with human validation baked in.

**Phase 4: Execution**

7. **Implementation Plan Doc** — Break down the refined spec into an actionable plan. Include task decomposition, effort estimates, dependency mapping, milestone definitions, and sprint/timeline planning. This bridges the gap between "what we'll build" and "how we'll actually execute it."

8. **Implementation** — Engineers build the solution according to the plan and spec. The detailed planning from previous phases helps implementation proceed smoothly, though real-world discoveries may still require spec updates.

9. **Testing** — The final validation phase ensures the implementation meets requirements through unit tests, integration tests, QA validation, performance testing, and verification against both the PRD objectives and technical spec requirements.

**Key Characteristics:**

- **AI-Augmented but Human-Validated**: The workflow embraces AI assistance for exploration and design while maintaining human oversight at critical junctures. This balances the speed and breadth of AI with the judgment and experience of senior engineers.

- **Separation of Concerns**: The workflow clearly distinguishes between product requirements (PRD), technical design (Spec), and execution planning (Plan Doc). This separation ensures each artifact serves its specific purpose without conflation.

- **Feedback Integration**: Unlike linear waterfall processes, this workflow explicitly includes a feedback loop after the initial spec, acknowledging that first drafts benefit from review and iteration.

- **Research-Driven**: Starting with deep research rather than jumping straight to requirements ensures decisions are grounded in solid understanding of the problem space.

This workflow is particularly well-suited for complex projects where upfront investment in planning pays dividends, teams working with AI coding tools, and organizations that want to leverage AI capabilities while maintaining human control over critical technical decisions.

### Advanced: Self-Correction Chains
Chain prompts so Claude reviews its own work, catching errors and refining outputs—especially valuable for high-stakes tasks.

### Optimization Tip
For independent subtasks (like analyzing multiple documents), create separate prompts and run them in parallel for speed.

---

## Long Context Tips

### Key Techniques

**1. Document Placement**
Place lengthy documents (100K+ tokens) at the beginning of prompts rather than at the end. Queries at the end can improve response quality by up to 30% in tests, especially with complex, multi-document inputs.

**2. Structural Organization**
Implement XML tags to organize multiple documents clearly. The recommended approach wraps each item in `<document>` tags containing `<document_content>` and `<source>` subtags, enabling better information retrieval.

Example:
```xml
<documents>
  <document>
    <source>Report A</source>
    <document_content>
      Content here...
    </document_content>
  </document>

  <document>
    <source>Report B</source>
    <document_content>
      Content here...
    </document_content>
  </document>
</documents>

Now analyze these documents and answer: [Your question here]
```

**3. Quote Grounding**
Request that Claude extract relevant quotes from source materials before completing the primary task. This method helps the model navigate through extraneous content and focus on pertinent information.

### Practical Example
For medical diagnostics, request quotes from patient records placed in `<quotes>` tags, followed by diagnostic analysis in `<info>` tags. This two-step approach ensures responses remain anchored to specific document passages.

### Context Window Advantage
Claude 4 models support 1 million token windows, enabling complex, data-rich analysis across multiple documents simultaneously—making these organizational techniques particularly valuable for sophisticated tasks.

---

## Extended Thinking Tips

### Core Prompting Techniques

**General Over Prescriptive Instructions**
Rather than providing step-by-step guidance, Claude performs better with high-level directives. Ask Claude to "think about this thoroughly and in great detail" and "consider multiple approaches" rather than numbering specific steps it must follow.

**Multishot Prompting**
When you provide examples using XML tags like `<thinking>` or `<scratchpad>`, Claude generalizes these patterns to its formal extended thinking process. This helps the model follow similar reasoning trajectories for new problems.

**Instruction Following Enhancement**
Extended thinking significantly improves how well Claude follows instructions by allowing it to reason about them internally before executing them in responses. For complex instructions, breaking them into numbered steps that Claude can methodically work through yields better results.

### Advanced Strategies

**Debugging and Steering**
You can examine Claude's thinking output to understand its logic, though this method isn't perfectly reliable. Importantly, you should not pass Claude's thinking back as user input, as this degrades performance.

**Long-Form Output Optimization**
For extensive content generation, explicitly request detailed outputs and increase both thinking budget and maximum token limits. For very long pieces (20,000+ words), request detailed outlines with paragraph-level word counts.

**Verification and Error Reduction**
Prompt Claude to verify its work with test cases before completion. For coding tasks, ask it to run through test scenarios within extended thinking itself.

### Technical Considerations
- Thinking tokens require a minimum budget of 1,024 tokens
- Extended thinking functions optimally in English
- With Claude 4's 1M token context window, thinking budgets can scale significantly higher (200K+ tokens are supported)
- Traditional chain-of-thought prompting with XML tags works for smaller thinking requirements
