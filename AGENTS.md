# Coding Guidelines for AI Agents

## Do not replace LINQ

Never replace LINQ expressions with manual loops, manual index tracking, or equivalent imperative code. LINQ is idiomatic C# and is preferred in this codebase. PRs that rewrite LINQ into `for`/`foreach` loops or manual aggregations will be rejected.

Examples of what is **not allowed**:
- Replacing `.Select(...)`, `.Where(...)`, `.Any(...)`, `.All(...)`, `.FirstOrDefault(...)`, `.ToList()`, etc. with manual loop equivalents.
- Replacing `string.Join(...)` with a `StringBuilder` loop when LINQ already expresses the intent clearly.

## General

- Follow the existing code style and patterns in each file.
- Do not introduce unnecessary abstractions or refactors beyond the scope of the task.
- Do not add comments that describe what the code does — only add comments when the *why* is non-obvious.
