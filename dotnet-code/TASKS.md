# Odoo 19 core (D:\Practice\custom\odoo-19.0\odoo) — continuous port backlog

Source: 537 .py files, ~177k lines (`odoo/` core + `odoo/addons/` 25 bundled
modules, 111MB). Porting every file 1:1 is not the goal — much of it is
WSGI/HTTP server internals, CLI tooling, test suites, and upgrade scripts
that don't apply to this .NET clone. This file tracks portable
**behaviors**, prioritized by value, worked through continuously across
turns until told to stop.

Scope: **`odoo/addons/` is skipped for now** (explicit instruction) — this
backlog covers `odoo/orm/`, `odoo/osv/`, `odoo/api/`, `odoo/service/`,
`odoo/modules/` only, i.e. framework internals, not addon-defined
models/views. Revisit `addons/` when told to.

Database: **Postgres only**, matching real Odoo (which never supported
anything else — see `odoo/sql_db.py`). `DatabaseAdapter` no longer has a
multi-provider abstraction; SqlServer/MySql/Sqlite/InMemory code paths were
removed outright, not just unused.

Convention: each item names the real Odoo source it's modeled on, what gets
built here, and any deliberate simplification (ponytail-style, noted inline
in code too).

## Done

- [x] RPC dispatch guard (`odoo/service/model.py` `get_public_method`) —
      `OdooModel.ExecuteMethod` now blocks framework hooks / inherited
      members, only model-declared actions are RPC-callable.
- [x] ORM domain operators (`odoo/orm/domains.py` `STANDARD_CONDITION_OPERATORS`)
      — added `>=`, `<=`, `like`, `not like`, `ilike`, `not ilike`, `=like`,
      `not =like`, `=ilike`, `not =ilike`, `in`, `not in` to
      `ModelRegistry.SearchRead`. (`any`/`not any` — relational sub-domain
      matching — deliberately skipped, see below.)
- [x] Module dependency graph (`odoo/addons/base/models/ir_module.py`
      `button_immediate_install`/uninstall — kept as prior art even though
      `addons/` is now out of scope) — `SetModuleState` cascades installs
      through `Depends` and blocks uninstalling a module other installed
      modules still depend on.
- [x] Apps/module manager UI, QWeb templating subset (`t-esc`, `t-if`/
      `t-elif`/`t-else`, `t-foreach`, `t-att-*`) wired to
      `/web/report/{model}/{template}/{id}`.
- [x] **Postgres-only migration**: `DatabaseAdapter` hardcoded to Npgsql,
      `DatabaseProvider` enum and all SqlServer/MySql/Sqlite/InMemory code
      removed from `ModelRegistry` (Create/Write/Unlink/SearchRead no
      longer branch on provider). `appsettings.json` points at the real
      local instance (`dotnet_odoo_db`).
- [x] **Bug found by the Postgres migration itself**: `XmlDataLoader`
      passed every XML field value as a raw string into `Create()`.
      SQLite/InMemory silently tolerated this; Postgres correctly rejected
      inserting text into an `integer`/`double precision` column
      (`42804`). Added `ConvertXmlFieldValue` — casts by the field's real
      `FieldDef.Type` (int/float/bool) before `Create()` runs. Verified
      live: full app boot + login + search_read + create + write +
      `in`-domain filter + unlink round-tripped against Postgres
      successfully.

- [x] **`@api.ondelete`** (`odoo/orm/decorators.py`) — `[ApiOndelete]` method
      attribute + `OdooModel.ValidateOndelete`, wired into `Unlink()` before
      the row is actually deleted. Example: `AccountMoveModel` now blocks
      deleting a posted/paid invoice. Verified live against Postgres.
- [x] **Exception hierarchy** (`odoo/exceptions.py`) — added `UserError`
      (422), `AccessError` (403), `MissingError` (404) alongside the
      existing `ValidationError` (400). `UniversalRpcController` unwraps
      `TargetInvocationException` once and maps each type to its real HTTP
      status instead of everything falling into 400/500.
- [x] **Required-field / Selection-value validation** (`odoo/orm/fields.py`
      `required`, `fields_selection.py`) — `Create`/`Write` now raise a
      friendly `ValidationError` instead of letting an invalid value hit
      Postgres as a raw NOT NULL / nothing-at-all failure. Verified live.
- [x] **`MissingError` on stale Write/Unlink** (`odoo/exceptions.py`
      `MissingError`) — `Write`/`Unlink` on an id that's already gone now
      raise 404 instead of silently returning `false`. Verified live.
- [x] **`Installable` enforcement + `auto_install` cascade**
      (`odoo/modules/module.py` manifest defaults) — `SetModuleState`
      refuses to install a module flagged `Installable: false`, and after
      any install, any discovered "glue" module whose `Depends` are now
      all satisfied and `AutoInstall: true` installs itself automatically.

- [x] **SQL injection fix** (not from the survey — found while auditing per
      explicit request): `model` in every RPC call (`req.Model` from the
      JSON body) flowed unvalidated into raw `FROM`/`INSERT INTO`/`UPDATE`/
      `DELETE` table-name interpolation in `SearchRead`/`Create`/`Write`/
      `Unlink` — column names were already safe (filtered against the real
      schema) but the table name wasn't. Added `ModelRegistry.EnsureModelExists`
      (throws `UserError`, matching Odoo's own `execute_cr`: `"if recs is
      None: raise UserError(...)"`), called at the top of all four entry
      points, plus defense-in-depth quote-escaping in `Quote()`. Verified
      live: `model` containing `"; DROP TABLE res_users;--` now 422s before
      any SQL is built; normal queries and the target table are unaffected.

- [x] **Audit columns (dates)** (`create_date`/`write_date`, `models.py`
      `MetaModel`) — every model now gets these two universal columns via
      `AutoSyncSchema`, server-set in UTC on `Create`/`Write` and never
      client-overridable. `create_uid`/`write_uid` deferred — needs a
      "current user" context threaded through `ModelRegistry`, same
      prerequisite as the `sudo()` item below.
- [x] **`active` field + default archive filter** (`models.py`
      `active_test`) — every model gets `active` (default `true`);
      `SearchRead` hides `active=false` rows unless the caller's domain
      explicitly mentions `active`. Pre-existing rows with no value yet
      (`NULL`, from before this migration) correctly still count as active.
- [x] **`copy()` / duplicate** — `ModelRegistry.Copy` (shallow clone: id/
      create_date/write_date/One2many lines excluded), wired as RPC method
      `copy` and a "Duplicate" button next to Save/Discard on every form.
      All of the above verified live against Postgres (create → audit
      columns present; archive → hidden by default, visible when asked;
      pre-existing row with NULL `active` still returned; duplicate
      produces a real independent new record).

- [x] **Many2many fields** (`odoo/orm/fields_relational.py`) — real
      `FieldType.Many2many` (appended as enum value 9, not inserted, so
      webclient.js's existing hardcoded numeric type checks don't shift).
      `DatabaseAdapter.ManyToManyTable` derives a join table + two id
      columns from `(model, fieldName, relation)`, auto-created in
      `AutoSyncPhysicalDatabase`. `SearchRead` resolves it to `[[id,name],
      ...]` tuples (consistent with how Many2one already reads); `Create`/
      `Write` sync the join table via `SyncMany2many` (accepts either raw
      ids or `[id,name]` tuples, so round-tripping a previous read works);
      `Unlink` cleans up join rows in both directions (this model's own
      m2m fields, and any other model's m2m field that references the
      deleted record) so nothing dangles. `Copy()` carries m2m values over
      to the duplicate, matching Odoo's default. Frontend: a native
      `<select multiple>` widget for field type 9, options loaded the same
      way Many2one's dropdown already is.

      **Used to retire the `res.users.group_ids` CSV-string hack**
      (`Security.cs`) — it's now a real many2many to `res.groups`;
      `SecurityGroups.Admin`/`Employee` changed from fake XML-id-style
      strings (`"base.group_system"`) to the group's actual `name`, since
      a real relation has no XML-id registry to match against.

      Verified live against Postgres: login resolves admin status through
      the real relation; `search_read`/`write` read and write `group_ids`
      correctly; deleting a referenced group cleanly drops it from every
      user's `group_ids` with no orphaned join-table row; `copy()` carries
      the relation to the duplicate. (One hiccup along the way: the `admin`
      user row pre-existed in this dev database from earlier test runs in
      this session, created before the migration, so it had no join-table
      entries and `AdminOnlyModels` blocked fixing it through the API
      itself — a real chicken-and-egg gap in that hardcoded gate, not
      something to silently work around. Fixed with a direct one-off SQL
      insert against the dev DB, the same way a real deployment would run
      a data-migration script — not a code change.)

      **Not done**: a dedicated Users/Groups management screen (no
      `res.users` menu/form view exists in any addon) — out of scope, this
      item was about the field type existing and working, not building UI
      for it. `AdminOnlyModels`' chicken-and-egg gap surfaced above is
      also unresolved — revisit once `sudo()`/superuser escalation (in the
      out-of-scope list) exists, so a bootstrap path doesn't require
      already being admin.

- [x] **Batch create** (`@api.model_create_multi`, `odoo/orm/decorators.py`)
      — `ModelRegistry.CreateMulti` accepts a list of dicts, calling the
      same `Create()` pipeline (validation/compute/constrain/many2many
      sync) per row and returning a list of ids. `UniversalRpcController`'s
      `create` case detects a JSON array vs. a single object in `args[0]`
      and dispatches to `CreateMulti`/`Create` accordingly - the existing
      single-record call shape is untouched.

      Verified live: single `create` still works unchanged; batch `create`
      with 3 rows returns `[id,id,id]` and all 3 exist; a batch with one
      invalid row (missing required field) correctly 400s.

      **Known gap, not fixed here**: `CreateMulti` isn't transactional.
      Each `Create()` call opens and commits on its own connection (true
      of every CRUD method in `ModelRegistry`, not just this one - there's
      no ambient transaction anywhere in this engine), so when row *N* in
      a batch fails, rows `1..N-1` stay committed instead of the whole
      batch rolling back - confirmed live (a 2-row batch where row 2 failed
      validation left row 1 committed; cleaned up manually after
      verifying). Real atomicity means wrapping an entire RPC call in one
      shared DB transaction across every `Create`/`Write`/`Unlink` it
      touches - a much larger architectural change than batch create
      itself, so deliberately not attempted in this pass.

## In progress / next up (priority order, core framework only)

1. [ ] **X2many write-commands** (`odoo/orm/commands.py`, the
       `(0,0,vals)`/`(4,id)`/`(6,0,ids)` tuple language) — currently
       `webclient.js` issues separate `create`/`write` calls per O2M line
       instead of one command list. Real behavior change, not just
       internals; needs a client + `Write`/`Create` change together.
2. [ ] **OR/NOT domain grouping** (`odoo/osv/expression.py`, `odoo/orm/domains.py`
       prefix notation: `['|', ('a','=',1), ('b','=',2)]`) — `SearchRead`'s
       domain today only ANDs a flat list of leaves; there's no way to
       express OR or a negated sub-group at all. Needs a small
       recursive-descent parse of the prefix-notation list into a tree
       before evaluating. **Deliberately not done this pass**: widening
       `SearchRead`'s `domain` parameter from `List<List<object>>` to
       `List<object>` risks breaking every existing call site that uses a
       C# collection-expression literal (`[["field","=",val]]`) — needs a
       careful, isolated pass with its own verification, not bundled into
       a larger batch.
3. [ ] **Many2one `ondelete` policy** (`odoo/orm/fields_relational.py`
       `Many2one` `ondelete=`) — `Unlink()` has no FK-safety net; deleting
       a referenced row either orphans child rows or throws a raw Postgres
       FK-violation exception instead of a friendly error / cascade.
4. [ ] **`_sql_constraints`** (`models.py` `_add_sql_constraints`) —
       declarative unique/check constraints with friendly error text
       instead of a raw Postgres unique-violation.
5. [ ] **`post_init_hook`/`uninstall_hook`** (`odoo/modules/loading.py`) —
       `IOdooAddon` has no lifecycle callback beyond `RegisterModels` for
       one-time data seeding/migration on install or cleanup on uninstall.
6. [ ] **`create_uid`/`write_uid`** (`models.py` `MetaModel`) — needs a
       "current user" context threaded through `ModelRegistry` CRUD calls
       from `UniversalRpcController`'s `HttpContext.User`; same prerequisite
       as `sudo()`/superuser escalation (see out-of-scope section).
7. [ ] **`any`/`not any` domain operator** — relational sub-domain
       matching, e.g. `[('invoice_line_ids','any',[('price_unit','>',100)])]`.
       Depends on #2 (domain tree) landing first. Lower priority than the
       above; genuinely needs a related-model subquery-style evaluation.

## Done (continued)

- [x] **Transactional CRUD** — `Create`/`Write`/`Unlink`/`SyncMany2many`
      now take an optional `IDbTransaction? tx = null` parameter (default
      preserves every existing call site unchanged) and share one
      connection/transaction when a caller passes one in. New
      `ModelRegistry.RunInTransaction<T>(Func<IDbTransaction,T>)` opens a
      connection+transaction, runs the work, commits on success, rolls
      back and rethrows on any failure. `CreateMulti` is now the first
      (and so far only) consumer — a batch create is genuinely atomic.

      **Design constraint that shaped this**: `ModelRegistry` is a DI
      *singleton*, shared across every concurrent request. A transaction
      can never be stored as mutable instance state on it — that would let
      one request's rollback reach into another request's in-flight
      writes. It has to be threaded explicitly through each call, which is
      why the API is an optional parameter rather than an ambient/ThreadLocal
      context.

      Verified live against Postgres, reproducing the exact scenario that
      exposed the gap: a 2-row batch create where row 2 fails validation
      now leaves **zero** rows committed (previously left row 1
      committed and orphaned). Confirmed a fully-valid batch still commits
      all rows, and single `create`/`write`/`unlink` (the `tx=null`
      default path) are unaffected.

      **Not done**: `Write`/`Unlink` have the same `tx` parameter and
      would compose correctly with `RunInTransaction`, but there's no
      batch-write/batch-unlink RPC entry point yet to actually exercise
      it — the plumbing is there for the next thing that needs it (e.g. a
      "create order + create lines atomically" flow), not proven live
      beyond `CreateMulti` itself.

## Explicitly out of scope

Confirmed via a full survey of `odoo/orm/`, `odoo/osv/`, `odoo/api/`,
`odoo/fields/`, `odoo/models/`, `odoo/modules/`, `odoo/service/`,
`odoo/_monkeypatches/`, and every direct `.py` file under `odoo/`:

- `D:\Practice\custom\odoo-19.0\odoo\addons\` (all 25 bundled modules) —
  skipped for now per explicit instruction.
- `http.py` (113KB WSGI server, session/router internals) — ASP.NET Core
  already is our HTTP layer.
- `service/db.py` (multi-database create/drop/dump/restore, master
  password) — single fixed database, no multi-tenant admin surface.
- `service/server.py` (WSGI/prefork/gevent process management, signal
  handlers, cron worker forking) — ASP.NET Core + no multi-worker model.
- `service/security.py` (Odoo's own session-token store) — superseded by
  ASP.NET cookie auth already in `UniversalRpcController`.
- `service/common.py` (`exp_version`/`exp_about` RPC) — tiny, no client
  needs server version negotiation here.
- `_monkeypatches/` — entirely Python-stdlib/3rd-party-library patching
  (ast, bs4, lxml, pytz, werkzeug, etc.), no C# equivalent need. One note
  folded in already: Odoo forces process-wide UTC; our chatter timestamps
  should stay consistent about this if it ever becomes visibly wrong.
- `loglevels.py`, `netsvc.py`, `release.py`, `init.py`, `__main__.py` —
  Python logging-config plumbing, deprecated encoding helpers, version
  constants, and the CLI entrypoint. Nothing portable found.
- `exceptions.py`'s `CacheMiss`, `ConcurrencyError`, `LockError`,
  `RedirectWarning` — no field-cache layer, no pessimistic locking, and no
  action-redirect UI concept exist here to make these meaningful yet.
- `orm/environments.py` `sudo()`/superuser context escalation — real gap,
  but would replace the `AdminOnlyModels` hardcoding with a `bool asAdmin`
  threaded through `ModelRegistry` CRUD; deferred until `ir.model.access`-
  style per-model ACLs exist (currently addons-scoped, out of scope).
- CLI (`cli/`), test suites (`tests/`), upgrade scripts (`upgrade/`,
  `upgrade_code/`) — not applicable to this project's shape.
- Multi-provider database abstraction — deliberately removed, not just
  unported. Postgres is the only supported database, matching real Odoo.
