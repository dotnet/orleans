# Quality Improvement Techniques

This document covers techniques for improving specific aspects of Claude's output quality: consistency, factual accuracy, and security.

## Reducing Hallucinations

### Core Definition
Language models like Claude can generate factually incorrect or contextually inconsistent text, a problem termed "hallucination." This guide provides strategies to minimize such issues.

### Basic Strategies

**1. Permission to Admit Uncertainty**
Allow Claude to say "I don't know" by explicitly granting permission to acknowledge uncertainty. This straightforward approach substantially reduces false information generation.

Example:
```
If you don't know the answer or are uncertain, please say so rather than guessing.
```

**2. Direct Quotation Grounding**
For very lengthy documents (100K+ tokens) or when working with multiple large documents, request that Claude extract verbatim passages before proceeding with analysis. This anchors responses to actual source material rather than inferred content.

Example:
```
First, find and quote the relevant passages from the document.
Then, based only on those quotes, provide your analysis.
```

**3. Citation Verification**
Make outputs traceable by requiring Claude to cite supporting quotes for each claim. The model should then verify claims by locating corroborating evidence; unsupported statements must be removed.

Example:
```
For each claim you make, provide a direct quote from the source material.
After drafting your response, verify that each claim has supporting evidence.
Remove any claims that cannot be substantiated with quotes.
```

### Advanced Approaches

**Step-by-step reasoning**
Request Claude explain its logic before providing final answers, exposing potentially flawed assumptions

**Multiple-run comparison**
Execute identical prompts several times and analyze outputs for inconsistencies suggesting hallucinations

**Progressive validation**
Use prior responses as foundation for follow-up queries asking for verification or expansion of statements

**Information source limitation**
Explicitly restrict Claude to provided materials, excluding general knowledge access

Example:
```
Use ONLY the information provided in the attached documents.
Do not use any external knowledge or general information.
If the documents don't contain the information needed to answer, say so.
```

### Important Caveat
While these techniques significantly reduce hallucinations, they don't eliminate them entirely. Always validate critical information, especially for high-stakes decisions.

---

## Increasing Consistency

### Core Techniques

**1. Format Specification**
Define desired output structures using JSON, XML, or custom templates. This approach ensures Claude understands all formatting requirements before generating responses.

Example JSON:
```json
{
  "sentiment": "positive|negative|neutral",
  "confidence": "high|medium|low",
  "key_themes": ["theme1", "theme2"],
  "summary": "Brief summary here"
}
```

Example XML:
```xml
<analysis>
  <sentiment>positive|negative|neutral</sentiment>
  <confidence>high|medium|low</confidence>
  <key_themes>
    <theme>theme1</theme>
    <theme>theme2</theme>
  </key_themes>
  <summary>Brief summary here</summary>
</analysis>
```

**2. Response Prefilling**
Begin the Assistant turn with your desired structure. This technique "bypasses Claude's friendly preamble and enforces your structure," making it particularly effective for standardized reports.

Example:
```
User: Analyze this customer feedback.
Assistant: {
```

This forces Claude to immediately start with the JSON structure.

**3. Example-Based Constraints**
Supply concrete examples of desired output. Examples train Claude's understanding better than abstract instructions alone.

**4. Retrieval-Grounded Responses**
For knowledge-dependent tasks, use retrieval mechanisms to anchor Claude's replies in fixed information sets. This maintains contextual consistency across multiple interactions.

**5. Prompt Chaining**
Decompose intricate workflows into sequential, focused subtasks. This prevents inconsistency errors by ensuring "each subtask gets Claude's full attention."

### Practical Applications

The guide demonstrates these techniques through real-world scenarios:
- **Customer feedback analysis**: Using JSON structures for consistent categorization
- **Sales report generation**: Via XML templates for standardized formatting
- **Competitive intelligence**: With structured formats for comparable analysis
- **IT support systems**: Leveraging knowledge bases for consistent responses

Each example illustrates how precise specifications and contextual grounding produce reliable, repeatable outputs suitable for scaled operations.

---

## Mitigating Jailbreaks and Prompt Injections

### Core Strategies

**1. Harmlessness Screening**
Pre-screen user inputs using a lightweight model like Claude Haiku for content moderation. Have the model evaluate whether submitted content "refers to harmful, illegal, or explicit activities" and respond with Y or N accordingly.

Example:
```
Evaluate the following user input. Does it refer to harmful, illegal, or explicit activities?
Respond with only Y or N.

User input: <user_input>{USER_INPUT}</user_input>
```

**2. Input Validation**
Filter prompts for jailbreaking patterns. You can use an LLM to create a generalized validation screen by providing known jailbreaking language as examples.

**3. Prompt Engineering**
Design system prompts that establish clear ethical boundaries. For instance, define organizational values including:
- "Integrity: Never deceive or aid in deception"
- "Compliance: Refuse any request that violates laws or our policies"

Example system prompt:
```
You are an AI assistant for [Company]. You must adhere to these values:

1. Integrity: Never deceive users or help them deceive others
2. Safety: Refuse requests for harmful, illegal, or explicit content
3. Compliance: Follow all applicable laws and company policies
4. Privacy: Protect user data and confidential information

If a request violates these values, politely explain why you cannot help and suggest an alternative approach if possible.
```

**4. User Accountability**
Monitor for repeated abuse attempts. If a user "triggers the same kind of refusal multiple times," communicate that their actions violate usage policies and take appropriate enforcement action.

**5. Continuous Monitoring**
Regularly analyze outputs for jailbreaking indicators and use findings to refine your validation strategies iteratively.

### Advanced Approach: Layered Protection

Combine multiple safeguards for enterprise applications. For example, in a financial services context, the system should sequentially:
1. Screen queries for compliance
2. Process legitimate requests
3. Refuse non-compliant ones with specific explanations

This multi-layered approach creates comprehensive defense without relying on any single security mechanism.

### Important Note
No single technique provides complete protection. A defense-in-depth approach combining multiple strategies provides the most robust security against jailbreaks and prompt injections.