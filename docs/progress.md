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

---

## Phase E — The canvas · **done**

A test drawn as a picture of what happens, and a picture that knows when it is not a test yet.

| | |
|---|---|
| Node catalogue | 72 types across Core, Data, Checks, Flow and Access — as data in five files, not seventy classes |
| Typed sockets | Control edges say "then"; data edges say "this value goes there". A mismatch is refused mid-drag |
| Graph storage | `WorkflowNode` and `WorkflowConnection` as rows in a `ScenarioVersion` — diffable, indexable, versionable |
| Validation | Twelve checks: no start, two starts, duplicate names, missing properties, dangling and mistyped edges, unreachable steps, cycles |
| Canvas | Vue Flow wearing the product's tokens; node anatomy with an eight-state ring, group hue, and diamond failure sockets |
| Inspector | The form for any node, built from its specification — including which fields to hide until they apply |
| Keyboard | `Ctrl+Z`/`Y`, `Ctrl+S`, `Ctrl+D`, `Ctrl+A`, `Delete`, with an undo counter in the bar |
| Verified | `e2e/demo-canvas.ts` draws a scenario through the interface — palette, drags, typed properties — until the validator says "Ready to run" |
| Tests | 365 passing — 285 unit, 39 integration, 41 component; 77 accessibility checks, 204 screenshots |

### Four decisions worth recording

**Seventy node types as data, not seventy classes.** The brief forbids hard-coding them in one file
and the better reason is what it buys: the canvas draws any node it is given, the inspector builds
its form from the specification, and adding one is a record in one file rather than a class, a form,
a palette entry and a switch case.

**Flow runs left to right even in Persian.** A flowchart's direction is a convention people already
hold, not a reading direction — mirroring it would make every diagram in every document about this
product wrong. The palette, the inspector and every word on a node are localised; the arrows are
not. Visible in the Persian screenshots.

**Type compatibility is narrow, and asymmetric for credentials.** `Any` widens both ways and nothing
else does — a Number that quietly becomes Text is a test comparing "200" with 200 and passing. A
`Secret` satisfies `Any` but nothing satisfies `Secret`, so a token can be passed along and a plain
string cannot be mistaken for one. Stated twice, on the server and in the browser, with a test on
each side using the same table.

**Every credential is a named reference, never a text box.** A password typed into a property would
be stored in the graph, exported with it, and visible in a screenshot of the canvas. A test walks
the catalogue looking for any property whose name suggests a credential and is not a `SecretRef`.

### What the run and the audit caught

**The validator was returning English prose.** It read fine until it appeared on a Persian canvas
next to Persian labels. Nothing in the engine knows about languages — correctly — so it now returns
a code and its arguments, and the web layer builds the sentence. Data-type names are localised too:
"produces a number and expects a response" only works if all four words are in one language.

**`.palette` was already taken by the command palette**, so the node palette inherited a dialog's
radius, shadow and 60vh cap and floated in the middle of its column.

**Vue Flow's zoom controls ship with no accessible name** — three buttons a screen reader calls
"button", which axe reports as critical. Replaced with our own, labelled.

**New nodes were laid down 24 pixels apart on a 216-pixel card**, so every one buried the last.

**The minimap painted every node black**: it draws onto a canvas element, where a CSS variable
resolves to nothing.

**A node's summary line is a URL for most nodes and a translated sentence for the rest.** Forcing
monospace and left-to-right onto both made the Persian ones read backwards.

---

## Phase F — The runner · **done**

A scenario that actually runs, watched while it happens, and readable afterwards.

| | |
|---|---|
| Engine | Iterative walk over the graph — branch, switch, skip, group, repeat, while, for-each, for-each-row, retry, poll, try/catch, cleanup, parallel, rate-limit, break, continue |
| Every node does something | All 72 catalogue types have behaviour: 54 executors and 18 handled by the runner. A test holds the two halves against the catalogue and fails if a type is added with nothing behind it |
| Bounded | 200,000 steps, 32 levels of nesting, a required ceiling on every loop. An unbounded test is a build agent that stops answering |
| Expressions | A comparison language, not a scripting one — deliberately, because this runs other people's tests on somebody else's machine |
| Record | `TestRun` keeps the graph it ran as a snapshot; node runs are per (node, iteration, attempt); events are sequenced rows, not a text blob |
| Live | SignalR per run, with a full read on connect and on reconnect — the console opens the same way on a run from last month and one that started two seconds ago |
| Worker | A bounded channel and a background service, one run at a time, cancellable, pinned to the run's workspace |
| Console | The graph with each step's state on it, a virtualised log that follows the tail until you scroll away, and a timeline of duration-length bars with retry segments |
| Verified | `e2e/demo-run.ts` presses Run on the canvas, waits for a verdict, and refuses to report if the run never finishes |
| Tests | 426 passing — 338 unit, 47 integration, 41 component; 85 accessibility checks, 216 screenshots |

### Five decisions worth recording

**A node that silently does nothing is worse than one that fails.** Twenty-eight of the seventy-two
palette types had no behaviour when the phase started: a scenario using them would come back green
having tested nothing, and nobody re-reads a green run. `Every_node_on_the_palette_does_something`
now fails the build if that happens again.

**A retried step that eventually worked did work.** The superseded attempt stays in the log and the
timeline — flaky detection in a later phase reads it from there — but it does not decide the run.
Otherwise the retry node would be a way of turning a flaky test into a slow failing one.

**Parallel branches really run at once, and the state is behind one lock.** What that does not
promise is that a step in one branch can read a step in another; that is a race in any test runner,
and the answer is not to write scenarios that way. Written down rather than left to be discovered.

**The run keeps its own copy of the graph.** The first thing anybody does after a failing run is
edit the scenario, and a report that changes when the test changes is not a report. An integration
test edits the scenario through the real save path and asserts the run is unmoved.

**`flow.forEachRow` has no concurrency setting.** It had one, and the runner cannot honour it: every
step publishes under one `{{steps.…}}` scope, so two rows at once would read each other's responses.
The knob was removed rather than left to do nothing. Rows do run concurrently in the capture engine,
where a row is one request and nothing is shared.

### What the run and the audit caught

**Enums were going over the wire as numbers.** The console dropped every log line and rendered an
empty graph, silently — `LEVELS.indexOf(1)` is -1, and `(3).toLowerCase()` throws mid-render. Both
DTOs now carry words, the hub serialises enums as strings, and the one place that still converts
warns rather than throwing.

**A failed run had an empty log.** A step that failed before it could log anything — an address that
would not resolve — left a red badge and a blank console. Every failure and every check now reaches
the log.

**The demo seed created no environments at all**, so the first thing anybody pressed Run on failed
with "environment has no value". Each demo project now gets a Local pointing at the built-in fake
API and a Staging that is deliberately unreachable.

**The canvas did not clip**, so a node past the right edge drew straight over the inspector — and
could not be clicked, because the inspector was on top of it.

**A one-node scenario opened at 2× zoom**, which made the next node somebody added land off the
surface. Fitting now has a ceiling, and a newly added node is brought into view.

**`.run-timeline-bar` named both the header strip and the duration bars**, so the header came out
painted in the running colour. The same collision as `.palette` in the last phase.

**Seven columns do not fit a phone.** The run history dropped two on narrow screens rather than
leaving the duration half off the edge.

**"Run from this node" is not built**, and is carried to Phase H rather than faked: it needs a
start-node override in the engine and the rerun trigger that phase builds anyway.

### Known debt, carried to Phase L

**The engine's prose is English.** Run outcomes, log lines and assertion descriptions are written in
the engine, which knows nothing about languages — so on a Persian console the banner reads
"Everything that was checked held." next to Persian labels. The fix is the treatment the graph
validator already had: a code and its arguments from the engine, the sentence from the web layer.
It is not done piecemeal on purpose — half of it would give a banner that is Persian for the common
cases and English whenever a step failed, which reads as broken rather than untranslated.

---

## Phase G — More than one place · **done**

The same tests, at the same moment, in two environments — and what differs between them.

| | |
|---|---|
| Batch | `RunBatch` is a grouping and nothing else; every cell of the grid is an ordinary `TestRun` |
| Matrix | N scenarios × M environments queued in one press, capped at 60 cells because each one is a real request to somebody's real API |
| Grid | A row per scenario, a column per environment, a cell that is a dot, a word and a duration — and a link to that run's console |
| State | Queued, Running, Passed, Failed — derived from the runs, never stored, so it cannot disagree with them |
| Comparison | The phase-C diff engine over two runs' responses, step by step, matched by node and iteration |
| Dynamic fields | The pair detector from phase C offers what looks like an id or a timestamp, so a reader can tell noise from a regression |
| Verified | `e2e/demo-matrix.ts` ticks the boxes, presses the button, waits for every cell to land, and opens a comparison |
| Tests | 433 passing — 338 unit, 54 integration, 41 component; 93 accessibility checks, 228 screenshots |

### Four decisions worth recording

**A batch is only a grouping.** A separate "matrix run" type would need its own record of what
happened, and then there would be two answers to "what did step three return" that could disagree.
Every cell is a `TestRun`, so clicking one opens the console phase F already built, with its log,
its timeline and its graph.

**The comparison is computed, not stored.** It is a reading of two runs that are both already
recorded; a saved copy would be a third answer able to contradict either.

**Steps are matched by node and iteration, never by position.** A branch that went one way in
staging and the other in production produces two different lists, and comparing them by index would
diff step three against step four and report nonsense with total confidence. Steps only one side
reached are listed rather than dropped — that is often the whole answer.

**No rules by default, and that is honest rather than lazy.** Two environments differ in ids,
timestamps and hostnames as a matter of course, and pretending to know which of those to hide would
eventually hide a real one. The dynamic-field detector offers them instead and the reader decides.

### What the run and the audit caught

**The two comparison pickers filled the toolbar.** `.select` is full width by default, which is
right in a form and wrong in a bar — two dropdowns as wide as the page read as the content rather
than a setting.

**The production column said "Production Production".** Most people call their production
environment "Production", so a badge saying so beside it is a stutter. It is an icon now, with the
word behind it for a screen reader, which also survives the column being called "live" or «اصلی».

**The diff viewer said "Identical to the baseline" where no baseline exists.** Its copy assumed the
one place it had been used. It takes a `subject` prop now, so the words fit the page and the
colours, layout and keyboard behaviour stay one component.

**The duration inside a cell failed contrast in dark mode** — the subtle grey that reads as
secondary on a page background does not survive a tinted chip. The hierarchy is carried by weight
instead.

**A red chip reading "1" did not say what the 1 was**, which is the project's own dot-colour-word
rule broken on the page that most needs it.

**The scenario and environment pickers wore the browser's default fieldset groove.** The element is
right — a screen reader should hear the legend before each choice — but its frame belongs to no
design system.

### Also fixed here

The demo seed created two environments, one of them unreachable, so a comparison between them had
nothing to show. It now seeds three: Local and Staging both answering the built-in fake API, which
is exactly the shape of a blue/green pair and makes a comparison demonstrate something, and
Production as a placeholder that deliberately does not resolve — a demo workspace must not have a
working route to anything anybody could mistake for real.

---

## Phase H — Nobody has to remember · **done**

Tests that run on their own, a door a build agent comes through, and the ones that cannot make up
their minds.

| | |
|---|---|
| Schedules | A cron expression, an IANA time zone, and a set of scenarios × environments — one instruction, one batch |
| Worker | Wakes every 30s, fires what is due, never catches up on what it missed |
| Keys | 256 random bits, stored only as a hash, shown exactly once, revocable and attributable |
| CI | `POST /api/v1/projects/{id}/runs` → 202; poll `finished`; fetch JUnit. No cookie, no browser |
| JUnit | One suite per run, environment in the suite name, seconds with a point, ISO Gregorian in every language |
| Flaky | Same scenario, same version, same environment, both passing and failing — over a fortnight, minimum three runs |
| Quarantine | The test still runs and still reports; its failures become skips. Never deleted, never hidden |
| Verified | `e2e/demo-ci.ts` issues a key through the interface, then does the rest over plain HTTP: 401 without it, 202 with it, poll, fetch, check the XML |
| Tests | 466 passing — 355 unit, 70 integration, 41 component; 101 accessibility checks, 252 screenshots |

### Five decisions worth recording

**A missed schedule fires once, not once per missed hour.** If the process was down for a day, an
hourly schedule fires once when it returns. A catch-up storm against somebody's production API is a
far worse failure than a missed window, and nobody has ever wanted the twenty-four.

**The schedule advances before the batch starts.** If starting throws, it has still moved on —
otherwise it stays due, is retried every thirty seconds, and turns one broken scenario into a
permanent load against whatever it points at.

**The time zone is stored, not derived.** "Every day at six" means six where the team is. The
author leaves and the schedule stays, so the zone belongs to the schedule.

**A key is hashed with plain SHA-256, deliberately.** This is a 256-bit random value, not a
person's password: there is nothing to brute force, and a slow hash on every CI request would be a
denial of service somebody built for themselves. What matters is that only the hash is stored.

**Quarantine reports failures as skips.** That is its whole meaning — the test still runs, still
records what it found, and stops being allowed to fail the build. Deleting a flaky test takes its
coverage away and nobody notices for six months.

### What the run and the audit caught

**"Next run" said "just now" for a time tomorrow morning.** `RelativeTime` deliberately clamps
future instants to zero — a stored timestamp in the future is clock skew — but a scheduled time in
the future is the whole point of that column. It has a forward-looking counterpart now, and the two
disagree about the same input on purpose.

**The same bug on the key expiry**, which read "just now" for a date ninety days out — the opposite
of the truth on the one column that governs when something stops working.

**The production environment read "Production Production"** in both pickers, the same stutter the
matrix header had. Marked by an icon now, which survives the column being called "live" or «اصلی».

**The keys card rendered outside the settings page's narrow column**, hanging off beside it.

**A route that already existed.** `/projects/{id}/settings` was not the dead nav link it looked
like — `ProjectsController.Settings` was already there, and a second controller on the same route
produced an ambiguous-match 500. The keys joined the page that existed.

---

## Phase I — Two people, or nobody · **done**

The rule that makes a review a review, the people it applies to, and the way back in for somebody
who has forgotten their password.

| | |
|---|---|
| Separation | Nobody approves a version they recorded — *while somebody else could*. Enforced in a service, not in a form |
| Roles | The capability table decides. A designer records and cannot approve; a reviewer approves and cannot record |
| Team page | Members, roles, invitations, and what each role can actually do — built from the capability table itself |
| Invitations | 32 random bytes, stored only as a hash, 14 days, one live link per address, withdrawable |
| Two rules | The last owner cannot be demoted or removed; nobody changes their own role |
| Approval inbox | Proposed versions and waiting sweeps in one list, per project, saying who recorded each and whether it was you |
| Audit filters | Who, and what kind — a GET, so a narrowed log is a URL somebody can send |
| Password reset | SMTP if configured; the same answer either way; single-use token; the link built from configuration, not the Host header |
| Verified | `e2e/demo-approval.ts` has the designer propose one version and the owner another, then a reviewer approves through the interface |
| Tests | 492 passing — 359 unit, 92 integration, 41 component; 117 accessibility checks, 300 screenshots |

### Five decisions worth recording

**The separation rule is conditional, and that is the design.** "Somebody else has to look, if there
is somebody else." A workspace of one person is not a governance failure — it is a workspace of one
person, and a product that refuses to let them approve anything is one they cannot use. The moment a
second person who *could* approve exists, the rule binds. It reads roles, not a head count: three
viewers and a designer is a workspace where nobody else can approve.

**The page says so before the press, not after.** A row you recorded shows "waiting on somebody
else" instead of an Approve button. Finding out a rule by being refused is a bad way to learn one.

**A colleague's password is never chosen for them.** An invitation carries a token, not an account:
the link says which workspace, the account says who, and following it while signed out sends you to
sign in and back again. A link that created accounts would be a link that creates accounts.

**The reset link is built from configuration, not from the request.** `App:PublicUrl`, because the
Host header is attacker-controlled: type a victim's address into the reset form with a forged Host
and they receive a genuine email from us pointing at somebody else's server. Everything about that
message is real except the domain.

**Four demo colleagues share the demo password rather than getting one each.** The demo workspace is
already behind a switch and a password somebody had to choose; four more secrets make that harder to
reason about, not safer. Without colleagues none of this phase can be seen at all.

### What the run and the audit caught

**`v@version.Number` rendered literally.** Razor reads `v@v…` as an email address and emits it
verbatim, so every row in the approval inbox said "orders v@version.Number". Parenthesised now.

**The audit filter answered the wrong question.** The parameter was called `action` — a reserved
routing token — so it bound to the method name, filtered for events starting with "Index", and
rendered an empty log with a Clear button on it. Nothing threw. It is called `kind` now, and the
comment says why.

**Sixteen audit actions had no sentence.** Every `apikey.*`, `matrix.*`, `run.*`, `schedule.*` and
`team.*` event recorded since Phase G rendered as its own key — "audit.action.team.roleChanged" —
because a missing label renders as itself and nothing complains. Two tests now scan the source for
`AuditEntry("…")` in both directions: every recorded action needs a label, and every label needs
something that records it. The second one found three `member.*` labels the code stopped using eight
phases ago.

**An unregistered icon renders as an empty box.** `mail-check` was never added to
`Scripts/lib/icons.ts` — the registry is hand-written because lucide's barrel costs 660 kB — so the
password-reset confirmation had a blank rounded square where its icon should be, and passed every
test including axe. There is a test for that now too.

**Switching language dropped the query string.** The reset link, an audit filter, a page number —
all lost, because the switcher was built from `Request.Path` alone.

**A role change asked in red.** The confirm dialog was danger-toned for everything; making somebody
a Reviewer is not a deletion. Red on everything teaches people that red means nothing.

### And one from Phase F, found here and fixed here

**A console open during a run never reached a verdict.** `e2e/demo-run.ts` timed out waiting for the
badge, while the runs list said Passed and a console loaded afterwards said Passed.

The console read the run's state, then opened the socket and joined the run's group. Between those
two things it is not listening — and a run against a local service finishes in thirty milliseconds,
which is inside the handshake. The terminal message went to a group the connection had not joined
yet, nothing polled, and the page sat on "Running" for ever while the database said otherwise.
Nothing threw. It now reads once more after joining, which closes exactly that window.

---

## Phase J — A project in a file, and four ways in · **done**

Taking a suite out of the building, bringing other people's in, and twelve places to start from.

| | |
|---|---|
| Format | Versioned, indented, camel-cased JSON. No GUIDs — everything refers to everything by slug, node ids renumbered n1…nN in draw order |
| Determinism | Two exports of an unchanged project are the same file; a moved node is a one-line diff |
| Secrets | Never. Not the value, not the ciphertext, not the nonce. Only the names, so the far side knows what to create |
| History | Not carried. No runs, no audit, no rejected proposals — those are the record of one installation |
| Import | Three pages: the file, what it would do, done. It adds and never overwrites; an imported schedule arrives switched off |
| cURL | A real shell parser — quoting, escapes, continuations — because a URL with `&` in it lives inside quotes |
| OpenAPI | 3.x, JSON or YAML, one scenario per operation, checked at the status the document itself calls success |
| Postman | v2.1, folders and all. Scripts are reported, not run |
| Credentials | All three do the same thing: the header stays pointing at `{{secrets.name}}`, the value is dropped on the floor |
| Templates | Twelve real graphs, each validated by the same validator the canvas uses; the card's drawing is generated from the graph |
| Verified | `e2e/demo-portability.ts` downloads this project, imports it back as a new one, imports a cURL command and an OpenAPI document, and starts two scenarios from templates |
| Tests | 612 passing — 465 unit, 106 integration, 41 component; 129 accessibility checks, 336 screenshots |

### Five decisions worth recording

**No identifier in the file.** A GUID is a fact about one installation's database. Putting one in a
file means two exports of the same project never match, a diff is unreadable, and importing into a
second instance either collides or silently makes orphans. Slugs everywhere, node ids renumbered.

**An import adds and never overwrites.** Anything whose slug is taken is left alone and counted, and
the preview says so before anybody presses anything. Merging would mean a file somebody was handed
can silently change a baseline that a schedule runs against production tonight, and there is no undo
for that. Somebody who wants the incoming version can delete theirs and import again.

**The name of a credential travels; the value does not.** Somebody pastes a working cURL command out
of their terminal and the bearer token is real. Writing it into a scenario would put it in the
database as plain text, in every export, in the diff viewer, and in the first screenshot anybody
takes of the page. So the header is kept pointing at `{{secrets.authorization}}`, the reader is told
which secret to make, and what they pasted is never stored.

**A template is checked by the validator, not by eye.** All twelve are asserted runnable in a test,
and the only thing one is allowed to be missing is the data set or baseline the card tells you to
choose. A template that will not run teaches somebody the product is broken before they have built
anything of their own.

**The card's drawing is made from the graph.** A stored picture has no way of being wrong — it
drifts the first time anybody edits a template and nothing notices. This one is built from the same
nodes the scenario is built from, so if the shape on the card is wrong, the scenario is wrong.

### What the run and the audit caught

**Every export was a different file.** Nothing orders the connections table, so the edges came back
in whatever order the database felt like — two projects with identical graphs could produce files
differing in nothing but line order, which is exactly the diff that makes people stop reading diffs.
Canonicalised in the same step that renumbers the nodes.

**`authToken` became `authtoken`.** The camel-caser lower-cased the whole first word, so a name
somebody had already written in that shape came out different — and the reference in the scenario
then did not match the secret they were told to create.

**`-d ''` turned a POST into a GET.** curl sends an empty body; the parser could not tell "no `-d` at
all" from "`-d` with nothing in it".

**"This is not JSON" and "this is not one of these" were the same message.** Parsed before
deserialised now, because the two send somebody to different places.

**Every driver quietly switched projects.** The project list is ordered by when it last changed, so
importing three files put three new projects above the demo one, and every script that took the
first card moved to a project with no runs and nothing to photograph. Nothing failed — the pages all
render — and the evidence would have looked complete. They name the project now, and match it
exactly: `Catalog API (2)` contains `Catalog API` and sorts above it.

**Contrast on the chosen import source.** Tinting the whole card put the help text on
`--accent-soft`, where `--ink-subtle` stops meeting contrast in dark mode. It is a ring now — a
tinted panel with body text on it is where contrast bugs live.

**The sketch had no edges.** They were stroked with `var(--border)`, which is not a token this
project has, so every line was invisible and nothing complained.

---

## Phase K — A machine somewhere else · **done**

A hosted ProofFlow cannot reach an API that lives inside somebody's network. The answers people
reach for otherwise are all worse — a VPN into the test tool, a hole in a firewall, production
credentials pasted into a form on the internet. A runner inverts the direction: nothing connects
inwards, and an agent on the inside asks whether there is work, does it, and reports.

| | |
|---|---|
| SSRF | The guard is in `ConnectCallback`, so it sees the address actually dialled rather than the one in the URL. DNS rebinding, redirects to loopback, IPv6 mapped forms, decimal and octal literals — all refused, all with tests that fail the build |
| Retention | Bodies, logs and artefacts age out on a per-project setting; what a run decided is kept for ever |
| Enrollment | A 4×4 code from an alphabet with no 0/O or 1/I/L, good for fifteen minutes, usable once, stored as a hash and shown exactly once |
| Credentials | The token is a hash; the per-runner signing key is sealed with the same cipher as a secret. Neither can be read back |
| Job | Signed HMAC-SHA256 over the payload as written, so an agent can answer the only question that matters to a process running arbitrary requests inside a private network: did this come from the installation I enrolled with, unchanged |
| Package | The graph, the environment, the variables and secrets the graph references, and the data sets and baselines it names — found by walking the graph. Not the project, not the workspace, not anything else the database holds |
| Execution | The same `ScenarioRunner`, the same `NodeExecutors`, the same `GuardedHttpExecutor`. The agent implements `IRunServices` over the package and `IRunSink` into memory; there is no second engine and a scenario cannot tell which side it ran on |
| Reporting | Node results, assertions, log lines and captures come home and are written as though the run had happened here — captures into the same review queue a local run would use |
| Runner UI | A page per workspace: the code set large enough to read aloud, a countdown, and five states as a dot plus a word that says what to do about it |
| Choosing one | On the environment, because that is where the fact lives: an API inside somebody's network is unreachable from here whoever is asking |
| Redaction | Both paths redact the resolved URL as well as the body, and a value the redactor removed is rendered as a chip rather than as a string |
| Verified | Seven tests run real scenarios through the shipped agent classes against a real HTTP server with no database anywhere; four more cover the page and the environment's choice |
| Tests | 643 passing — 500 unit, 142 integration, 43 component; 113 accessibility checks, 336 screenshots |

### What making it real caught

**Every job package would have arrived empty.** The tenant filter is fixed when `IWorkspaceScope` is
first resolved, and by then the request had already built a `DbContext` to check the runner's token
with — as an anonymous caller, in no workspace. Every read after that returned nothing: no
environment, no data sets, no baselines, handed to an agent that would run it and report a failure
nobody could explain. The runner calls take their own scope after the token is read now, the way the
workers do.

**A secret used in a URL was written out in full.** The body was redacted; the resolved URL beside it
was not — and that URL goes into the step's output, the console and the report. The remote path
surfaced it and the local path had it too.

**A countdown template rendered into an attribute took the page down.** `IViewLocalizer` formats at
write time, so a string still holding its `{0}` cannot be emitted as an attribute value. The element
carries translation keys now and the behaviour looks them up — which is what the rest of the
JavaScript already did.

**The card's expiry never showed, in Persian.** It travelled through `TempData` as an ISO string and
was read back with `DateTimeOffset.TryParse` under the current culture — the Persian calendar. Only
the code crosses the redirect now; the expiry is read from the row the page is already showing, so
the card and the list cannot disagree either.

**The table shrank instead of scrolling.** Five columns and two buttons on a phone put the
description one word per line and pushed the actions off the end. The wrapper already scrolled; it
had nothing to scroll.

---

## Phase L — The parts a person touches · **done**

Prioritised by what the acceptance run put in front of a reader, not by the length of a list.

| | |
|---|---|
| Run from a step | Begins at the step you chose and does not re-run what came before. Only a top-level step: a loop body is driven by its container, and entering at it would run it once, outside the thing that gives its iteration meaning. A step that needs an earlier step's output gets the same refusal as a name that does not exist, which is the honest answer and one you can act on by starting further back |
| Saying so | The console carries "From «Fetch the order» onwards". Three steps in a scenario of nine reads as a run that fell over unless the page says otherwise, and «passed» on a third of a test is a different sentence |
| Naming a test | The canvas heading is the name, editable in place. The rename endpoint already existed with nothing calling it, so every scenario anybody drew stayed «Untitled scenario» — in the runs list, on the dashboard, and in the JUnit a build agent reads |
| Getting started | Four steps on the dashboard, read from the database rather than remembered, and gone once the fourth is true. What a project is, what an environment is, what a test is, where the result lives. Nothing to dismiss |
| Dialogs | Focus is trapped while one is open, returns to whatever opened it, the dialog carries its own title as its accessible name, and Escape works after a click on the backdrop. `aria-modal` was promising all four and delivering none |
| Virtual scrolling | Where a list genuinely gets long: the run log already windows, and runs and activity page at thirty and fifty. Not everywhere |
| Tests | 645 passing — 502 unit, 143 integration, 43 component; 125 accessibility checks, 348 screenshots |

### What the dashboard was saying

**Zero runs, zero failures, zero waiting.** Hard-coded, with a comment saying the real numbers arrive
with the phases that produce them. Those phases arrived several phases ago. The first page anybody
opens was telling a workspace with forty runs that it had none — and the project's own design
contract is that a false zero is worse than a dash.

**Zero environments on every project card.** The card has counted fields and nobody filled them.

**A "Recent runs" panel that had never shown a run.** It is a table now, across every project,
because the question it answers — did anything break while I was away — is not a question about one
project.

### Two things left undone, on purpose

**The engine's messages stay in English.** They are written for a non-programmer — "Expected 200, got
500", "«vars» has no value, so «recordId» cannot be read from it" — and the one family that already
carries a code, an HTTP request that could not be made, is localised where somebody first meets it.
Translating the rest means giving the engine a message code and persisting it on every node run and
every assertion: a new taxonomy and a schema change, which is the thing this phase was told not to
build. Backlog.

**A data set shows its first two hundred rows.** It says so, and pasting replaces the whole set, so
the way out exists. Virtualising an editable table is a real piece of work for a case that has a
workaround; the read-only log was worth it, this is not yet. Backlog.

---

## Phase M — Somewhere to run it, and a way back · **done**

| | |
|---|---|
| Deployment | One image, one compose file, one app container and a database. `cp .env.example .env`, fill in two values, `docker compose up --build` |
| One container, deliberately | The run queue is in memory, so a second replica would run a second scheduler and a second sweeper against the same database while its own queue sat empty. `ProofFlow.Worker` refuses to start for the same reason, and says so |
| Verified | In CI, because Docker is not on the machine this was built on: the `docker` job builds the image, brings the stack up with `--wait`, and fails unless the application answers on `/healthz` and serves a page with its built assets on it |
| Backup | The database and the keyring. Nothing else — artefacts and payloads are rows, not files |
| Restore | Verified on a real installation: backed up, deleted the database and the keyring, confirmed the account was gone, restored both, signed in, and revealed a stored secret to its original value. Signing in proves the database came back; revealing a secret is the only thing that proves the master key did |
| Upgrade | Verified with a database created and filled by the previous release, handed to this build, which applied the migration it was missing on start and came up with everything intact |
| Documentation | [operations.md](operations.md) — deploy, back up, restore, upgrade. It says which steps were verified and where |
| Acceptance | Twenty steps, from an empty project, through the interface, on both the local runner and a real agent process. `e2e/acceptance.ts` |
| Tests | 645 passing — 502 unit, 143 integration, 43 component; 125 accessibility checks, 348 screenshots |

### What this phase found

**A backup of `proofflow.db` would have restored an almost empty product.** SQLite runs in WAL mode
and on the installation this was verified against the database file was 4 KB while the write-ahead
log beside it was 1.4 MB. Copying the one file everybody thinks of as "the database" loses almost
everything, and does it without an error. The procedure says to copy the directory.

**The Worker was a loaded gun.** Its comment said the schedulers "are registered by the phases that
introduce them" — they were, in the shared infrastructure registration that both processes call. So
running it beside the web application meant two schedulers deciding independently that a nightly
suite was due, and two sweepers deleting the same rows, while its own run queue sat empty. It
refuses to start now and explains why.

**The README described a deployment that did not exist**, listing the worker as "the test runner and
scheduler, as their own process".

### The hardening pass, and what it did not find

Checked, because they are the things that would actually matter here: the fake API and the design
reference are both gated to Development and neither is mounted in Production; the import path holds
an uploaded file's name as data and never builds a path from it; the runner API authenticates every
call against a token and acts only in that runner's workspace; the export carries no secret, and
there are tests for all four. The two real leaks this pass would have looked for — a secret in a
resolved URL, a job package with no tenant — were found earlier, by making the agent run for real.

Nothing new. That is the honest result rather than a shortage of looking.
