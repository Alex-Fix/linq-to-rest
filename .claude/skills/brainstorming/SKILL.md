---
name: brainstorming
description: Turn a rough RestToLinq feature idea into an agreed design before any code is written — clarify with one question at a time, check it against the project's hard constraints (EF Core translatability, untrusted input limits, public API stability), compare 2–3 approaches, then write a design doc to docs/plans/. Use when the user wants to brainstorm, explore, or design a feature (new operator, syntax, integration), or says "let's think about…", "how should we…", "what if…", or starts a new feature without a clear spec.
---

# Brainstorming

Goal: leave with a **design the user has approved**, written down. Don't write code, scaffold files or change
config during brainstorming. The output is a design doc.

## 1. Understand the context first (silently)
Before asking anything, read what's relevant so you don't ask what the repo already answers:
- `CLAUDE.md`, `docs/plans/` (earlier designs, especially `2026-10-18-resttolinq-v1-design.md`)
- The code the feature would touch, and `git log --oneline -15`

## 2. Clarify — one question at a time
- Ask **one** question per message. Wait for the answer before the next.
- Prefer multiple choice (use AskUserQuestion with 2–4 options, recommended option first) over open questions.
- Focus on: who it's for (API author vs client developer), the core use case, client LINQ and wire (JSON tree /
  `$batch`) shape, what "done" looks like, and what's explicitly out of scope.
- Stop asking once you could explain the feature back in two sentences. Usually 3–6 questions.
- If an answer touches a concept the user may not know (expression trees, client evaluation, keyset
  pagination, unit of work), explain it plainly in 2–4 lines.

## 3. Check against RestToLinq's hard constraints
Every idea gets checked against these before approaches are proposed. Name any conflict out loud.

| Constraint | Ask |
|---|---|
| **Translatable** | Does the generated expression translate on EF Core (SQL Server, PostgreSQL, SQLite)? Any provider-specific gaps? Would anything fall back to in-memory evaluation? |
| **Untrusted input** | Can a client use it to reach members outside the DTO, bypass a policy filter, write a non-writable member, blow up nesting/node count, force a full scan or huge result/batch? Which limit bounds it? (→ `security` skill) |
| **No dynamic code** | Can it be built from `Expression` nodes alone — no string-built LINQ, `Compile()`, reflection over arbitrary members? |
| **Wire contract** | Does it fit the closed JSON node list / `$batch` format (one-key nodes, member paths, `"v"`)? New node or breaking change? Can the client LINQ provider express it, and does it fail locally when it can't? |
| **Public API** | New public types/members? Breaking change for existing package users? Configurable or opinionated default? |
| **Dependencies** | Do Abstractions/Client stay BCL-only? Which package owns it (Abstractions / Client / Server)? |
| **Errors** | What does the client get for invalid input — ProblemDetails with a constant message, never echoing raw input? |

## 4. Propose 2–3 approaches
For each: one-paragraph summary, what changes (Abstractions / Client / Server / samples), trade-offs,
rough size (S/M/L), and the constraint risks from step 3. **Lead with your recommendation and say why.**
Let the user pick or mix. Don't list options you wouldn't actually recommend just to reach three.

## 5. Present the design in small sections
Walk through the chosen approach in chunks of ~200–300 words, checking after each: "Does this look right so far?"
Cover what applies:
- Client LINQ usage and the JSON / `$batch` it produces (valid and invalid examples)
- AST / type checking and server translation (expression shape produced)
- Configuration surface (allow-lists, limits, per-type options) and defaults
- Public API additions
- Error handling (what the client sees)
- Testing (unit tests per package, SQLite translation tests, end-to-end client + server tests)
- Docs/README and sample updates

Go back to earlier steps whenever something doesn't fit. YAGNI: cut anything not needed for the core use case.

## 6. Write the design doc
When the user approves, write `docs/plans/YYYY-MM-DD-<topic>-design.md` (today's date):

```markdown
# <Feature> — design
Status: approved · Date: YYYY-MM-DD

## Problem
## Goals / Non-goals
## Constraints check        (table from step 3, with this feature's answers)
## Chosen approach          (+ why not the alternatives, one line each)
## Design                   (sections from step 5)
## Public API changes
## Security notes           (limits, allow-listing, error messages)
## Testing
## Open questions
## Implementation steps     (ordered, each small enough for one commit)
```

Then ask whether to start implementing — on a feature branch (`feat/<topic>`), following the implementation steps.
Don't commit the design doc unless asked.

## Anti-patterns
- Asking several questions in one message.
- Jumping to code or file scaffolding before approval.
- Presenting the whole design in one wall of text.
- Silently dropping a constraint conflict instead of raising it.
- Re-asking things already decided in `CLAUDE.md`, `docs/plans/`, or memory.
