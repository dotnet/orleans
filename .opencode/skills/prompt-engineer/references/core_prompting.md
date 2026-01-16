# Core Prompting Techniques

This document covers fundamental prompt engineering techniques that form the foundation of effective Claude interactions.

## Be Clear and Direct

### Core Principle
Think of Claude as "a brilliant but very new employee (with amnesia) who needs explicit instructions." The better you explain what you want, the better Claude performs.

### The Golden Rule
Show your prompt to a colleague with minimal context and ask them to follow the instructions. If they're confused, Claude likely will be too.

### Key Techniques

**1. Provide Context**
- Explain what the results will be used for
- Identify the intended audience
- Describe where the task fits in your workflow
- Define what successful completion looks like

**2. Be Specific About Output**
Explicitly state formatting requirements (e.g., "output only code and nothing else")

**3. Use Sequential Instructions**
Structure requests with numbered lists or bullet points to ensure Claude follows your exact process.

### Practical Examples

**Anonymizing Feedback**
- ❌ Vague: "Remove personal information"
- ✅ Specific: "Replace all names with [NAME], email addresses with [EMAIL], phone numbers with [PHONE], and locations with [LOCATION]"

**Marketing Emails**
- ❌ Unclear: "Write a marketing email"
- ✅ Detailed: "Write a marketing email to enterprise customers about our new security features. Tone: professional but approachable. Highlight: SSO, audit logs, and compliance certifications. Include a CTA to schedule a demo."

**Incident Reports**
- ❌ Generic: "Summarize this incident"
- ✅ Terse: "Extract: timestamp, severity, affected systems, root cause, resolution. Output as bullet points only."

### Key Insight
Precision prevents hallucination and ensures Claude delivers exactly what you need.

---

## System Prompts and Role Prompting

### Core Technique
Use the `system` parameter to assign Claude a specific professional identity. This transforms Claude from a general assistant into a specialized expert in a particular domain.

### Key Benefits
- **Enhanced accuracy** in complex domains like legal analysis or financial modeling
- **Tailored tone** adjusted to match the assigned role's communication style
- **Improved focus** keeping Claude aligned with task-specific requirements

### Best Practice
"Use the `system` parameter to set Claude's role. Put everything else, like task-specific instructions, in the `user` turn instead."

### Experimentation is Key
Roles can significantly impact outputs. A "data scientist" provides different insights than a "marketing strategist" analyzing identical information. Adding specificity—such as "data scientist specializing in customer insight analysis for Fortune 500 companies"—yields even more tailored results.

### Real-World Examples

**Legal Contract Analysis**
- Without role: Surface-level summaries
- With role (General Counsel at Fortune 500 tech company): Identifies critical risks like unfavorable indemnification clauses, inadequate liability caps, IP ownership concerns

**Financial Analysis**
- Without role: Basic observations
- With role (CFO of high-growth SaaS company): Strategic insights including segment performance, margin implications, cash runway calculations, actionable recommendations

---

## Using XML Tags

### Core Purpose
XML tags help Claude parse prompts more accurately by clearly separating different components like context, instructions, and examples.

### Key Benefits

1. **Clarity** - Clearly separate different parts of your prompt and ensure your prompt is well structured
2. **Accuracy** - Reduces misinterpretation errors in prompt components
3. **Flexibility** - Simplifies modifying or reorganizing prompt sections
4. **Parseability** - Makes extracting specific response sections easier through post-processing

### Best Practices

**1. Maintain Consistency**
Apply identical tag names throughout and reference them when discussing content

**2. Utilize Nesting**
Arrange tags hierarchically for complex information structures

**3. Common Tag Patterns**
```xml
<context>Background information</context>
<instructions>What to do</instructions>
<examples>Sample inputs/outputs</examples>
<documents>Long-form content</documents>
<thinking>Claude's reasoning process</thinking>
<answer>Final response</answer>
```

### Advanced Technique
Combining XML tags with multishot prompting or chain of thought methods creates super-structured, high-performance prompts.

### Practical Impact

**Financial Reporting**
- Without tags: Disorganized narrative
- With tags: Concise, list-formatted reports

**Legal Analysis**
- Without tags: Scattered observations
- With tags: Organized findings and actionable recommendations

### Important Note
No specific XML tags are canonically required—tag names should align logically with their content.
