# PokeTokenBar (VS Code port) — project instructions

This file is loaded in full every session, so it holds only rules that **always apply**.
Architecture, security invariants and the dependency policy live in
[`dotnet/README.md`](dotnet/README.md); how to run and package it is in
[`README.md`](README.md).

The Swift sources this was ported from are no longer in this repository. They remain upstream at
[chattymin/PokeTokenBar](https://github.com/chattymin/PokeTokenBar) if a behaviour needs checking
against the original.

## Layout

| Path | Rule |
|---|---|
| `dotnet/src/PokeTokenBar.Core` | Parsing, aggregation, pricing, companion. **No UI, no host, no VS Code.** Kept host-agnostic so a tray app could reuse it. |
| `dotnet/src/PokeTokenBar.Sidecar` | stdio JSON-RPC host. Owns credentials and **all** network access. |
| `extension/` | Renders. Holds no credentials and makes no network calls. |

## The trust boundary is the thing to protect

A folder the user opens can contribute configuration, and this process reads other tools' logs
and credentials. Two rules follow, and neither is negotiable:

- **The host never tells the sidecar what to do.** No protocol parameter may name a path, a URL,
  an endpoint or an executable. The sidecar resolves all of those itself.
- **Credentials live only in the sidecar.** Never in `SecretStorage`, never over the wire, never
  in a log.

Anything rendered in the webview is untrusted input, whatever its source, and is escaped at the
point of use even when the sidecar already sanitised it.

## Language

English only, in code, comments, commit messages and PRs — including when the instruction to
commit or open a PR arrives in another language. This is a squash-merge repository, so a PR
title becomes a commit on the default branch.

## Commits carry the context

Rationale, measurements and what a faithful port would have got wrong belong in the **commit
message**. Comments are for what a reader needs at the point of use: API contracts, non-obvious
constraints, decisions that look wrong until explained. Porting archaeology and change
commentary do not belong in the code.

## When a defect appears

Whatever surfaced it — a report, a review, a test, your own reading — do all four, in order.
The goal is never meeting the same *class* of mistake twice.

1. **Root cause, including the review gap.** Symptom, then direct cause, then *why the existing
   tests did not catch it*. Usually a test passed through a different path than the defect
   triggers, which is worse than no test because it grants false confidence.
2. **Sweep the class.** Grep the whole codebase for the same misuse or pattern. Never fix one
   instance and stop.
3. **Regression test the triggering branch.** Reproduce the exact condition, not a neighbouring
   one. For an `A || B` gate, exercise **B alone**. Then break the fix on purpose and confirm the
   test fails — a test never seen red proves nothing, and coverage percentages are not evidence.
   **Before adding a guard, check the trigger is reachable**: trace who can supply that input.
   An unreachable guard is over-specification, and withdrawing a review comment costs nothing.
4. **Capture it mechanically.** A test, a build gate, a script — something a machine enforces.
   Documentation is the weakest form and should be the last resort.

### Classes already paid for

Inherited from the original plus found during the port. Check this list before sweeping.

- **32-bit accumulators.** Swift's `Int` is 64-bit; C#'s `int` is not. Real corpora exceed
  `int.MaxValue`, and one balance constant does not fit in an `int` at all.
- **Unbounded reads of external files.** Stream and cap both file size *and* line length; a
  file-size cap alone still admits one pathological line.
- **Implausible numbers from external logs.** Discard rather than clamp — a clamped value goes
  on to dominate every aggregate it reaches.
- **Duplicate records.** Streaming re-emits the same turn; deduplicate, keeping the largest.
- **Absolute readings treated as deltas.** Use a watermark, and reset it on a day boundary.
- **Shared state across processes.** Every window runs its own sidecar; guard read-modify-write.
- **AOT-hostile serialisation.** Only the `JsonTypeInfo` overloads survive publishing; options
  carrying a generated resolver do not. Property initialisers do not survive deserialisation, so
  a new field's *default value* must be correct for existing saves.
- **Verifying absence instead of presence.** A packaged artifact needs checking for what must be
  in it, not only for what must not.
- **Silently truncated external data.** An out-of-range element should end a sequence, not
  discard it; a failed fetch should be marked for retry, not persisted as fact. In a tree, it
  ends the sequence only when no in-range sibling remains — prune it, never cut its parent off
  into a stub branch of its own.

## Never launch VS Code from a shell

On Windows every window is a child of one `Code.exe`, so a tool-invoked `code ...` can close the
user's entire session. Keep security-relevant logic in modules that do not import `vscode` so it
can be driven from plain node; use `@vscode/test-electron` if a real editor is genuinely needed.
