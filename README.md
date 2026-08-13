# ProofFlow

Prove your API still does what it did yesterday.

ProofFlow is a self-hosted platform for building, running and reviewing API tests **without writing
code**. It does two things, and the sidebar is two words long because of it.

**Endpoints** are the simple job. One request, kept: send it, read what came back, agree that it is
right. Give it a list of inputs — two thousand study identifiers, or five — and one button sends it
once per row and tells you which answers moved, field by field, with the parts that change on every
call marked as changing. Bring a Postman collection over and every request in it arrives here.

**Scenarios** are chains. Sign in, keep the token, read a list, take an id out of it, read that one,
put the API back the way it was. Drag the steps together on a canvas, feed each one's output into
the next, and run the whole thing against as many environments as you have.

A scenario can name what it needs answering — an order id, a page, a customer — and ask for it when
it runs. The same values go in the body of the API call that starts a run, so a pipeline supplies
them the way curl would. And if a workspace supplies a model key, describing a test in a sentence
gets a first draft drawn on the canvas, unsaved, for somebody to read and change.

> **Status: in development.** [docs/progress.md](docs/progress.md) states plainly what works today
> and what does not. Nothing in this README describes a feature that is not implemented.

---

## Architecture in one paragraph

One ASP.NET Core solution, one deployment, one origin, one authentication system. Razor renders the
pages; Vue 3 is embedded as **islands**, mounted only onto the regions that are genuinely
interactive, compiled by Vite straight into `wwwroot/build`. No Node process runs in production and
there is no separate SPA. Persistence is EF Core over PostgreSQL, with SQLite as a zero-install
target for development and tests. The part that actually runs a test — `ProofFlow.TestEngine` —
knows nothing about the database or the web, so a scenario can be executed inside a unit test.

| Layer | Project | Holds |
|-------|---------|-------|
| Domain | `ProofFlow.Domain` | Entities and enums. No dependencies at all. |
| Contracts | `ProofFlow.Contracts` | Versioned wire and export formats. |
| Application | `ProofFlow.Application` | Ports and use cases. |
| Engine | `ProofFlow.TestEngine` | Compiler, executor, assertions, normaliser, semantic diff, nodes. |
| Infrastructure | `ProofFlow.Infrastructure` | EF Core, migrations, identity, HTTP, secrets, jobs. |
| Web | `ProofFlow.Web` | MVC, Razor, internal API, SignalR, and the Vue/Vite frontend. |
| Worker | `ProofFlow.Worker` | A second home for the background work, for the day the run queue is shared. It refuses to start today, because running it beside the web application means two schedulers on one database. |

`ArchitectureTests` enforces the direction of those arrows on every build.

## Deploying it

One container and a database:

```bash
cp .env.example .env    # fill in the two values it will not start without
```

```bash
docker compose up --build
```

Then <http://localhost:8080>. Backing it up, restoring it and upgrading it are in
[docs/operations.md](docs/operations.md), which also says which of those have been verified and
where.

## Running it from source

Nothing to install beyond the .NET 10 SDK and Node 22. The default database is a SQLite file
created on first run.

```bash
cd src/ProofFlow.Web && npm install && npm run build && dotnet run
```

Or open `ProofFlow.slnx` in Visual Studio and press F5. Either way it comes up seeded, on
<http://localhost:5290>, with the account printed on the sign-in page and a **Quick start** at the
top of the sidebar — one button there makes a project pointed at the pretend API this application
serves, so there is something to send a request to before there is anything to configure.

The one prerequisite is `npm install && npm run build` in `src/ProofFlow.Web`, once. The .NET build
does not run Vite, so without it the pages render without their stylesheet.

### The account it comes with

```
demo@proofflow.local
ProofFlow!Demo2026
```

Development only, and stated on the page itself so nobody has to look it up. Four colleagues share
the same password — `reviewer@`, `designer@`, `runner@`, `viewer@` — for trying what each role can
and cannot do.

Anywhere but Development this account does not exist: the password has no default outside
development, and a seed with no password creates nothing and says so in the log. Choose one
deliberately if a demo instance is wanted somewhere real:

```bash
dotnet user-secrets set "Demo:Password" "<choose one>" --project src/ProofFlow.Web
```

Turning it off entirely:

```bash
dotnet user-secrets set "Demo:Seed" "false" --project src/ProofFlow.Web
```

It arrives with three projects and one scenario that runs: **Add a product, read it back, and clear
up** — sign in, keep the token, add a product, read a page of them, take an id out of the list, read
that one, and delete what it added. Twelve steps and five checks, against the fake API this
repository serves, so it passes as often as you press Run.

### Against PostgreSQL

PostgreSQL is the supported production target and the one that gets `jsonb` payload storage. Supply
the connection string outside the repository so a password never lands in a commit.

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Database=proofflow;Username=proofflow;Password=..." --project src/ProofFlow.Web
```

```bash
dotnet user-secrets set "Database:Provider" "postgres" --project src/ProofFlow.Web
```

Then apply the migrations explicitly. `Database:AutoMigrate` is off by default for PostgreSQL:
with more than one instance behind a load balancer, two of them applying DDL at once is worse than
a refusal to start.

```bash
dotnet ef database update --project src/ProofFlow.Infrastructure --startup-project src/ProofFlow.Web --context PostgresProofFlowDbContext
```

The two providers keep **separate migration sets**, because they disagree about column types for
JSON and for timestamps. Adding a model change means adding both:

```bash
dotnet ef migrations add <Name> --project src/ProofFlow.Infrastructure --startup-project src/ProofFlow.Infrastructure --context SqliteProofFlowDbContext --output-dir Persistence/Migrations/Sqlite
```

```bash
dotnet ef migrations add <Name> --project src/ProofFlow.Infrastructure --startup-project src/ProofFlow.Infrastructure --context PostgresProofFlowDbContext --output-dir Persistence/Migrations/Postgres
```

## Tests

```bash
dotnet test ProofFlow.slnx
```

```bash
cd src/ProofFlow.Web && npm test
```

## Screenshots for review

With the application running, this captures every page in both languages, both themes and three
widths — which is the only way the failures that matter get caught, because a heading that fits in
English overflows in Persian and a border that reads on white vanishes on black.

```bash
cd src/ProofFlow.Web && PROOFFLOW_PASSWORD=<demo password> npx tsx e2e/shoot.ts
```

Output lands in `docs/ui/raw`, which is git-ignored: a screenshot of a running instance can hold a
real token or a real address.

## Languages

Persian and English, right-to-left and left-to-right, from one catalogue in
`src/ProofFlow.Web/Resources`. Every string is a key in both files, and
`TranslationCompletenessTests` fails the build when one of them is missing a key, has an orphan, or
disagrees about a `{0}` placeholder. That test exists because a missing translation does not throw,
does not log, and renders as perfectly valid English in the middle of a Persian page.

## Licence

Not yet chosen.
