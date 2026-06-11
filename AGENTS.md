# Coding Guidelines for AI Agents

## Do not replace LINQ

Never replace LINQ expressions with manual loops, manual index tracking, or equivalent imperative code. LINQ is idiomatic C# and is preferred in this codebase. PRs that rewrite LINQ into `for`/`foreach` loops or manual aggregations will be rejected.

Examples of what is **not allowed**:
- Replacing `.Select(...)`, `.Where(...)`, `.Any(...)`, `.All(...)`, `.FirstOrDefault(...)`, `.ToList()`, etc. with manual loop equivalents.
- Replacing `string.Join(...)` with a `StringBuilder` loop when LINQ already expresses the intent clearly.

## Do not "fix" random scope IDs

Never replace `Random.Shared.NextDouble()` scope/lock identifiers (in `Context.cs`, `AsyncAsymmetricLock.cs`) with `Guid.NewGuid()` or other "cryptographically secure" alternatives, and never open PRs claiming this is a vulnerability. It is not one:

- These IDs are internal async-flow identifiers, never exposed to or supplied by untrusted parties. There is no adversary, so cryptographic security is irrelevant.
- Collision probability is governed by entropy alone: 53 random bits means a collision becomes likely only at ~95 million *simultaneously live* scopes (birthday bound). This is not a realistic concern.
- `Guid.NewGuid()` is also random, not "mathematically guaranteed unique" — it just has more bits. Swapping it in fixes nothing.

PRs proposing this change will be closed without review.

## General

- Follow the existing code style and patterns in each file.
- Do not introduce unnecessary abstractions or refactors beyond the scope of the task.
- Do not add comments that describe what the code does — only add comments when the *why* is non-obvious.
