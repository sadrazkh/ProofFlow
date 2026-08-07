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

## Phase C — Assertions, baselines, semantic diff · **done, with the interface**

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
| Tests | 278 passing — 227 unit, 30 integration, 21 component |

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

---

## D-C — The interface over it · **done**

Baselines list, baseline detail with a version rail, and the workbench island: replay, read what
moved, decide.

| | |
|---|---|
| Diff viewer | Virtualised from the first line — fixed 30px rows over a spacer, only what is on screen exists. Clickable summary chips jump to the first of their kind; `n` and `p` step the findings |
| Two layouts | Inline, and side-by-side above 900px. The choice is withdrawn below that width rather than offered and ignored |
| Rule builder | Twenty matchers in five named groups, each with a sentence of help; only the parameters that matcher needs are shown |
| Suggestions | Read off the compared response, every box clear, with the evidence and the effect spelled out beside each one |
| Capture | "Save as baseline" in the request lab stores the response *and* the unresolved request, so it can be replayed |
| Lifecycle | Capture approves the first version; every version after it is proposed and needs an approver |
| Verified | `e2e/demo.ts` drives the whole loop through the interface: capture from `/fake/volatile`, compare, turn the three dynamic fields into rules, compare again — identical |

### What the screenshots and the audit caught

**Lucide was replacing Vue's own nodes, and it broke reactivity everywhere.** `<i data-lucide>`
works in Razor, where markup is static. Inside a component it is poison: Vue created that `<i>` and
holds a reference, lucide swaps in an `<svg>`, and the next patch operates on a node that has left
the document. The symptom was not a missing icon — Vue patches an element's children before its
props, so the throw abandoned the rest of the render and the side-by-side toggle turned on while
the rows it controls never changed. Islands now render icons as real vnodes through `lib/Icon.ts`;
`createIcons` still serves the server-rendered markup, where it is safe.

**The default JSON encoder escapes everything outside ASCII.** Every value in every diff went
through `ToJsonString()`, so `+00:00` read as `+00:00` and a Persian response would have read
as `یک` — an entire class of API made unreadable by the one screen built to make
responses readable, in the language this product's first users work in.

**Five icons were used in markup and never registered**, so they rendered as nothing at all: the
previous-difference chevron, the suggestions lamp, the camera on "save as baseline", the production
shield and the forbidden padlock. The registry is explicit on purpose — the barrel import costs
660 kB — and the cost of that is exactly this, so it now has a test.

**A missing translation key renders as itself and survives review**, because `environment.title` in
a table heading looks like a variable name somebody expects to see. Two tests now close it: every
literal key in Razor and in the components must exist, and every member of `MatcherKind`,
`DiffKind`, `DynamicReason`, `Confidence` and `BaselineStatus` must have a string.

**Switching a matcher kept the old parameter when the new one had a slot for it.** A tolerance of
±5 moved from NumericTolerance to ArrayCount became "at least five items" — a rule nobody wrote,
in a row that reads as though it were just retyped. Parameters clear unconditionally now.

**The "ignored" summary chip sat at 4.33:1.** Grey on grey, and it is the one category that says a
check was deliberately turned off, so it has to be legible. It moved to the palette's documented
readable floor.

### Two decisions worth recording

**The compared response is held in memory, not in TempData and not in the database.** Accepting
three fields has to merge from the exact bytes that produced the diff on screen; fetching again
would merge from a response nobody reviewed, and on anything with a clock in it that is a different
response every time. TempData here is cookie-backed — a 200 KB body becomes fifty cookies and the
next request dies on a header limit. It is scratch, worth nothing ten minutes later, and a rejected
comparison should leave no trace, so: a memory cache keyed by user and baseline, with an expiry and
a size cap, and a clear message when it has gone.

**Suggestions save as rules on their own, without proposing a version.** The commonest comparison
is one where every difference is a field that changes by itself: the right answer is three rules and
no new version. Folding that into "save as a new version" would bless a timestamp as a baseline
value. The two buttons say two different things because they mean two different things.

---

## Phase D — Sample-based regression, capture mode, guided wizard · **done**

Section 5's case, working: two thousand identifiers, two thousand calls, two thousand answers to
file — and each one with its own idea of what correct looks like.

| | |
|---|---|
| Data sets | `DataSet` / `DataSetVersion` / `DataSetRow`, versioned so a report can say which data it ran against |
| Paste | One value per line, comma- or tab-separated, or JSON — guessed, shown, and overrulable before anything is imported |
| Capture | One request per row, four in flight, written in chunks so a cancelled sweep keeps what it has |
| Per-input answers | `BaselineSample`: one approved response per data-set key, written by approving what a sweep captured |
| Review queue | Six states, bulk selection, and `j` `k` `a` `r` `x` — the work is a keyboard task and the diff sits beside the list |
| Wizard | Nine steps, one question each, resumable from the browser after a reload |
| Verified | `e2e/demo-regression.ts` walks all nine steps: paste twelve rows, define the baseline, sweep, approve twelve samples — through the interface, against the local fake API |
| Tests | 308 passing — 240 unit, 39 integration, 29 component; 69 accessibility checks, 192 screenshots |

### Four decisions worth recording

**A sample-based baseline has no version, and that is not a gap.** Its request necessarily contains
`{{dataset.current.…}}`, which cannot be sent from the request lab — there is no current row there.
So "send it, then save what came back" is a path that does not exist for this kind of test, and
`POST baselines/define` is its own door: a request and a set of rules, with the answers living per
input in `BaselineSamples`.

**Four requests in flight, not as many as possible.** The thing on the other end is somebody's real
API, often the one their customers are using, and a test tool that opens two hundred connections to
it is a denial of service with a friendly name.

**The paste parser looks for agreement among most lines, not all of them.** Demanding unanimity on
the raw delimiter count sounds stricter and is worse in both directions: one quoted comma inside one
value made a real table read as a plain list, and so did a single row with a missing cell — the case
the problem list exists to report. It counts delimiters outside quotes and takes the modal count.

**A duplicate key is numbered apart rather than refused.** Two rows with the same key would mean two
approved answers for one input, which the baseline cannot hold — but the rows are still worth
keeping, so the editor says how many before saving and the server disambiguates.

### What the run and the audit caught

**The wizard could not see the data set it had just created.** The list came with the page, so a set
made at step five was not in it, and step six was permanently unable to start — the exact path the
wizard exists to make possible. The version it creates is now held separately.

**Step seven was a dead end after a reload.** The sweep summary lived only in memory while
everything else was persisted, so coming back to the review step showed a heading and nothing else.

**The cursor tint failed contrast in dark mode**, on the one row somebody is reading. At 18% the
purple wash lifted the background enough that `--ink-subtle` fell to 4.25:1; it is 10% now, and the
inset accent bar was always the primary marker.

**The fake API had nothing to sweep.** Its catalog is behind a bearer check and its list shuffles;
`/stable` takes no input and `/volatile` changes every call. `/fake/records/{id}` was added for
exactly this: an answer that varies by input and by nothing else, so a sweep reporting one
difference has found a real one.
