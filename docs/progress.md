# Progress

What works today, and what does not. Updated at the end of every phase.

> **Start here each session.** The work is driven by two plans that live in this repository:
> [plan/00-master-plan.md](plan/00-master-plan.md) (architecture and the functional phases A–M) and
> [plan/01-design-plan.md](plan/01-design-plan.md) (the design analysis, the design contract, and a
> design item-set per phase, D-0 through D-M). A session picks the current phase from this file,
> takes its items from **both** plans — D-0 first while it is still open — and follows the
> execution protocol in section 0 of the design plan.

The brief this is built against is large — thirteen phases, seventy-odd node types, a semantic diff
engine, a graph canvas, scheduling, CI integration, a private runner agent. Rather than build all of
it thinly, each phase ends with something that genuinely runs and is tested, so any point on this
list is a working product rather than a skeleton.

The measure of done is the twenty-step scenario in §21 of the brief: create a project, define two
environments, capture baselines from several IDs, ignore the dynamic fields by clicking them, run it
again, review the diff, accept part and reject part, build a multi-step workflow on a canvas, run it
across both environments, and trigger the same suite from CI — without writing a line of code.

---

## Phase A — Foundation · **done**

| | |
|---|---|
| Solution | Seven projects with the layering enforced by `ArchitectureTests` |
| Database | EF Core 10, SQLite and PostgreSQL, separate migration sets, both generated |
| Tenancy | Global query filter per workspace, with an explicit system scope for background work |
| Identity | Cookie sign-in, sign-up creates the first workspace, capability-based authorization |
| Languages | Persian and English, RTL and LTR, one JSON catalogue, completeness tested |
| Theme | Light, dark and system, applied before first paint |
| Interface | Design tokens, app shell, command palette, dashboard, projects, activity log |
| Tests | 57 passing — architecture, tenancy, provider compatibility, roles, slugs, navigation, HTTP smoke |

**Verified by running it.** The application starts on SQLite, migrates, seeds a demo workspace, signs
in, and renders every page in both languages, both themes and three widths. 84 screenshots were
reviewed; two defects found and fixed (a four-tile summary row breaking into 3+1, and Latin
punctuation landing on the wrong end of a line inside Persian cards).

**One real bug caught in the process.** SQLite refuses to `ORDER BY` a `DateTimeOffset`, and every
list in the application is ordered by a timestamp — so the development and test provider failed on
sign-in, the dashboard, the project list and the activity log. Fixed systemically with a value
conversion to fixed-width UTC text rather than per query, and `ProviderCompatibilityTests` now runs
the real queries against SQLite.

### Not built yet, and knowingly so

- The dashboard's pass rate, failing count and run trend read zero because nothing has run yet.
  They show `—` rather than `0`, because "no failures" and "nothing has run" are different claims.
- A project page has no tabs. Environments, scenarios, baselines and runs arrive in the phases that
  create them; eleven tabs leading nowhere would be worse than the honest empty state that is there.
- `ProofFlow.Worker` starts, connects and idles. It has nothing to run until Phase F.
- No Docker image yet. Note that this machine has no Docker daemon, so when one is written it can
  only be verified in CI — that will be said plainly rather than implied.

---

## Phase B — Environments, secrets, HTTP execution · **engine done, interface next**

### Done and tested

| | |
|---|---|
| Domain | `ProjectEnvironment`, `EnvironmentVariable`, `Secret`, with migrations for both providers |
| Secrets | AES-256-GCM, fresh nonce per value, key version recorded, generated key persisted |
| SSRF guard | Policy check before the request **and** an address check at connect time |
| HTTP executor | Manual redirect following, size cap, timeout, retry with the attempt count kept |
| Redaction | Values this run used, plus JWT / provider-key / self-naming-JSON patterns |
| Variables | `{{environment.x}}`, `{{secrets.x}}`, `{{vars.x}}`, `{{steps.a.response.b[0].c}}`, `{{dataset…}}`, `{{run.id}}` |
| Fake API | Login, categories, category fields, product CRUD, paging, slow, flaky, poll-until, redirect, oversized |
| Tests | 168 passing — 145 unit, 23 integration against a real socket |

The integration suite runs a genuine login → read-categories chain through the resolver, and proves
the SSRF guard refuses a **redirect** to `169.254.169.254` — which is the case that matters, because
`HttpClient`'s own redirect handling runs no policy at all.

### Two bugs caught while building it, both worth recording

The Data Protection fallback for the master key derived a *different* key on every start, because
`Protect` includes fresh randomness. Nothing would have failed loudly: every secret stored before a
restart would simply have become an authentication error somewhere else, days later. Replaced with
a generated key persisted to a file, and there is a test that stands in for a restart.

SQLite refuses to `ORDER BY` a `DateTimeOffset` (found in Phase A, recorded here because the same
class of problem will recur): the two providers disagree, and only running the real queries on both
finds it.

### Not built yet

The interface for all of the above — environment and secret screens, the request builder, and the
response viewer you can click a field in. The engine underneath them is complete and tested, so
these are views over working machinery rather than new mechanism.
