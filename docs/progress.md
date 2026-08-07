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

## D-0 — Design corrections · **done**

The eleven items from section 3 of the [design plan](plan/01-design-plan.md), taken before the
Phase B interface so the components it needs are settled first.

| | |
|---|---|
| Dates | Jalali in Persian, Gregorian in English, the reader's own time zone, relative under a week |
| Audit | Signing in is recorded — the translation key had been sitting unused |
| Reference | `/design` renders every component, development only, and is in the screenshot matrix |
| Stacking | A named z-index scale; no raw integers left in any stylesheet |
| Tooltips | Semantic tokens instead of a hard-coded near-black that was invisible in dark mode |
| Islands | Mounting reserves the placeholder's height, so nothing jumps when a component appears |
| Keyboard | The palette is a real combobox; menus take arrow keys and return focus on Escape |
| Icons | Raster sizes generated from the one SVG, plus a web manifest |
| Seed | Demo descriptions in the configured language rather than English inside a Persian panel |
| Gate | axe on every page, both themes, both languages — 33 checks, wired into CI |

**Four real bugs surfaced, three of them by the new gate.**

`aria-pressed` was being written onto `<html>`. The pre-paint theme script marked the document with
`data-theme-choice`, the same attribute name the theme buttons use to declare their value, so
`querySelectorAll('[data-theme-choice]')` matched the document element. Two meanings under one
name; they have two names now.

Five colours failed contrast, and the interesting one is `--ink-faint` at 2.63:1. Raising it to
pass 4.5:1 would have required a value indistinguishable from `--ink-subtle`, collapsing the bottom
of the ramp — so the role split instead: `--ink-faint` is now for disabled controls and decorative
marks, which the standard exempts, and everything a person reads moved up a step. The destructive
button was white on red in both themes: 4.21:1 light, 2.97:1 dark. It has its own pair now.

**The `/design` page rendered completely unstyled and the accessibility suite still passed.** A
frontend rebuild changes Vite's hashed filenames; `ViteManifest` read the manifest once at startup,
so the running application kept serving files that no longer existed. Unstyled HTML is black on
white, which passes every contrast rule there is — a green gate for the worst possible reason. The
manifest now reloads on change in development, and the suite refuses to audit a page whose
stylesheet is missing.

**And the suite was auditing the wrong pages.** It signed in once per test — twenty times against
an endpoint rate-limited to twelve a minute. Eight were refused, and a refused sign-in leaves the
browser on the sign-in page, so eight tests audited *that* and reported it under the name of a page
they never opened. All eight passed. There is one sign-in for the whole run now, and every test
asserts which page it is actually on before measuring anything.

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

### The interface · **done**

| | |
|---|---|
| Environments | Master-detail with the selection in the query string, so the page is a real URL |
| Reach | The three settings that widen it are grouped, each with its consequence on the line below |
| Variables | Project-wide or per-environment, shown in the reference form people paste |
| Secrets | Encrypted, listed as four characters, revealed only with the capability and always audited |
| Request | Verb chip, live reference checking, query and header tables, real sending |
| Response | Status, duration, size; a clickable JSON tree; a designed state for every failure kind |
| Fake API | Moved to `src/` and hosted by the web application in development |

**It genuinely runs.** A request built in the browser reaches the fake API through an environment
and comes back as a 200 with a browsable tree. A reference that does not exist is marked red as it
is typed and refused by the server before a socket opens, naming the reference and why.

Note on where the fake API lives: it moved from `tests/` to `src/` because the web application
hosts it in development. The brief asks for a demo that runs with no internet and no second
terminal, and a project under `tests/` that ships in the dev experience was mislabelled.

### Four things worth recording

**The production cookie policy is real, and the tests were signing in over plain HTTP.** Outside
development the session cookie is marked Secure, so it is never offered over `http://` — the tests
signed in successfully and arrived at the next page anonymous. They run over `https://localhost`
now, which keeps the production policy under test rather than switching the host to development to
dodge it.

**Icons vanished whenever Vue re-rendered.** The icon pass ran on insertion and on an explicit
event, neither of which fires when a component re-renders its own markup — so the response
viewer's failure state shipped with an empty circle. A mutation observer, batched to one pass per
frame, covers every island now.

**No built-in role separates storing a secret from reading one.** `ManageSecret` and `ViewSecret`
are distinct capabilities and are checked separately, but Owner and Admin hold both and nobody else
holds either. That is defensible — a test designer writing production tokens into environments is
not something to enable by default — and the separation becomes reachable when custom roles arrive
in Phase I.

**Placeholders cannot be dimmed to distinguish them.** They are text and must meet the same
contrast, which in a borderless key/value table made every example look like a value already
entered. They are italic instead.

---

## Phase C — Assertions, baselines, semantic diff · **engine done, interface next**

### Done and tested

| | |
|---|---|
| Matchers | All twenty from §6 of the brief — ignore, type-only, regex, tolerances, subset, array strategies, counts |
| Rule paths | `$.field`, `$.items[*].x`, `$.items[0].x`, `$..field`, `$['odd key']`, and `$` for the whole document |
| Diff | Six categories over a two-tree walk: added, removed, changed, type-changed, order-changed, rule violation — plus ignored, which stays visible |
| Arrays | Ordered, unordered, and matched by key — the last reports the one field that moved instead of everything after position two |
| Suggestions | GUIDs, JWTs, signed URLs, timestamps and trace ids, graded by confidence and never applied |
| Assertions | Status, header, JSON field with any matcher, JSON Schema, response time, body contains |
| Domain | `Baseline`, `BaselineVersion`, `BaselineRule` with the six-state lifecycle, migrations on both providers |
| Tests | 248 passing — 218 unit, 30 integration |

The comparison is structural, which is the whole point: key order, whitespace and `1` versus `1.0`
are not differences, and a shuffled list under an unordered rule reports as order-changed rather
than as every row differing.

### Three decisions worth recording

**Rule paths are matched by our own pattern rather than by the JsonPath library**, because the
question runs the other way. JsonPath answers "which nodes match this expression?"; the diff walks
two documents together and needs "does any rule apply to where I am standing?", asked thousands of
times against a path it is already building. The library is still used for assertions, where the
question genuinely is find-me-the-nodes.

**System.Text.Json stops at 64 levels by default — the same depth the walk stops at.** A payload
past the limit failed to parse, fell through to a text comparison, and two identical monsters
compared equal with the depth never mentioned. Parsing is allowed four times the walk's budget so
the walk's own limit is the one that fires and explains itself.

**A suggestion is never applied.** The detector grades its confidence so the interface can pre-tick
the certain ones, but an unticked row does nothing — a field silently excluded is a field that
stopped being checked without anyone deciding to.

### Not built yet

The interface: the diff viewer with its summary bar and n/p navigation, the rule builder, the
suggestion list, field-level accept and reject, and the version timeline. That is **D-C** in the
[design plan](plan/01-design-plan.md). The engine underneath is complete and tested, so these are
views over working machinery — the same shape Phase B took.
