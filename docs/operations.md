# Running ProofFlow

Deploying it, backing it up, getting it back, and upgrading it. Everything here has been done rather
than described; where something was verified somewhere other than a developer's machine, it says so.

---

## Deploying

One container and a database. That is the whole thing:

```bash
cp .env.example .env
```

Fill in the two values it refuses to start without — a database password and a master key — then:

```bash
docker compose up --build
```

Open <http://localhost:8080> and create the first account, which also creates the workspace. There
is no seeded password, because a well-known password that ships in an image is the same as none.

### One app container, on purpose

The run queue is in memory. A second replica pointed at the same database would not share the work:
it would run a second scheduler deciding independently that a nightly suite is due, and a second
retention sweeper deleting the same rows, while its own run queue sat empty. `--scale app=2` is not
how this gets faster; a larger machine is.

`src/ProofFlow.Worker` exists for the day that changes, and refuses to start until then. Running it
beside the web application is the failure above, so it says so and exits.

### Behind a reverse proxy

`ASPNETCORE_FORWARDEDHEADERS_ENABLED` is already set in the compose file. Without it every link the
application generates comes out `http://` on an installation reached over HTTPS.

### The two things that are not the database

| | |
|---|---|
| `keys` volume | The Data Protection keyring. It signs the authentication cookie: lose it and everybody is signed out on the next deploy, copy it between installations and their sessions become interchangeable |
| `PROOFFLOW_MASTER_KEY` | What every secret is encrypted with — the credentials your tests send to your own APIs, and the signing key of every runner. Changing it does not re-encrypt anything; it makes what is already stored unreadable |

Both belong in your password manager alongside the database password. A database restored without
the master key restores rows nobody can decrypt.

---

## Backing up

Two things: the database, and the keyring.

### PostgreSQL — the compose deployment

```bash
docker compose exec -T db pg_dump -U proofflow -Fc proofflow > proofflow-$(date +%F).dump
```

```bash
docker run --rm -v proofflow_keys:/keys -v "$PWD:/out" alpine tar czf /out/proofflow-keys.tgz -C /keys .
```

And the master key, which is in your `.env` and not in either of those.

### SQLite — the single-file deployment

Stop the application first, then copy the **whole `App_Data` directory**, not just the `.db` file.

This matters more than it sounds. SQLite runs in WAL mode, and on a live installation almost
everything is in the write-ahead log rather than in the database file: on the installation this
procedure was verified against, `proofflow.db` was 4 KB and `proofflow.db-wal` was 1.4 MB. A backup
of `proofflow.db` alone restores an almost empty product, and does it without any error at all.

```bash
cp -r App_Data keys /somewhere/safe/
```

Without stopping it, take a consistent copy through SQLite itself instead:

```bash
sqlite3 App_Data/proofflow.db ".backup '/somewhere/safe/proofflow.db'"
```

---

## Restoring

Stop the application, put both back, start it.

```bash
docker compose down
docker compose up -d db
cat proofflow-2026-08-09.dump | docker compose exec -T db pg_restore -U proofflow -d proofflow --clean --if-exists
docker compose up -d
```

For SQLite, replace `App_Data` and `keys` with the copies and start.

### Checking that it worked

Signing in proves the database came back. It does not prove the master key did — those are two
different files and the failure mode of getting the second one wrong is silent until somebody runs
a test that uses a credential.

So: sign in, open an environment that has a secret, and reveal one. A restored installation that
can show you the value of a secret has both halves.

**Verified**, on a real installation with a real encrypted secret in it: backed up, deleted the
database and the keyring, confirmed the account no longer existed, restored both, signed in, and
revealed the secret to its original value.

---

## Upgrading

Pull the new image and bring it up. On the compose deployment `Database__AutoMigrate` is on — one
process, so there is nobody to race with — and the schema is brought forward on start.

```bash
docker compose pull
docker compose up -d --build
```

Take a backup first. Not because migrations here are dangerous, but because that is the only moment
you can still choose the old version.

On a multi-instance PostgreSQL installation `Database:AutoMigrate` is off by default and the
migrations are applied deliberately, before the new version starts:

```bash
dotnet ef database update --project src/ProofFlow.Infrastructure --startup-project src/ProofFlow.Web --context PostgresProofFlowDbContext
```

**Verified**: a database created and filled by the previous release, handed to the current build,
which applied the migration it was missing on start and came up with every project, account and
session intact.

---

## Where things run

Docker is not installed on the machine this was built on, so the container is exercised in CI
instead: the `docker` job in [.github/workflows/ci.yml](../.github/workflows/ci.yml) builds the
image, brings the stack up with `docker compose up --wait` exactly as above, and fails unless the
application answers on `/healthz` and serves a page with its built assets on it. The PostgreSQL
migrations are applied in CI too, for the same reason — nothing on a laptop ever runs them.
