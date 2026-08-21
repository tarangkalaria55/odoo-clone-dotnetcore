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

## In progress / next up (priority order, core framework only)

1. [ ] **Many2many fields** (`odoo/orm/fields_relational.py`) — a real
       `FieldType.Many2many` with a join-table representation. Today
       `res.users.group_ids` is a CSV-string hack (documented in
       `Security.cs`); this would replace that and support tags/multi-select
       relations generally. Needs a webclient.js multi-select widget too
       (currently the type-5 relational `<select>` is single-value only).
2. [ ] **X2many write-commands** (`odoo/orm/commands.py`, the
       `(0,0,vals)`/`(4,id)`/`(6,0,ids)` tuple language) — currently
       `webclient.js` issues separate `create`/`write` calls per O2M line
       instead of one command list. Real behavior change, not just
       internals; needs a client + `Write`/`Create` change together.
3. [ ] **OR/NOT domain grouping** (`odoo/osv/expression.py`, `odoo/orm/domains.py`
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
4. [ ] **Many2one `ondelete` policy** (`odoo/orm/fields_relational.py`
       `Many2one` `ondelete=`) — `Unlink()` has no FK-safety net; deleting
       a referenced row either orphans child rows or throws a raw Postgres
       FK-violation exception instead of a friendly error / cascade.
5. [ ] **Batch create** (`@api.model_create_multi`, `decorators.py`) —
       `Create()` is single-record only; no list-of-dicts create path.
6. [ ] **Audit columns** (`create_uid`/`create_date`/`write_uid`/
       `write_date`, `models.py` `MetaModel`) — no baseline who/when trail
       on any record.
7. [ ] **`active` field + default archive filter** (`models.py`
       `active_test`) — soft-delete via `active=false`, auto-excluded from
       `SearchRead` unless asked for; today only hard `Unlink` exists.
8. [ ] **`_sql_constraints`** (`models.py` `_add_sql_constraints`) —
       declarative unique/check constraints with friendly error text
       instead of a raw Postgres unique-violation.
9. [ ] **`copy()` / duplicate** (`models.py`) — no record-clone action;
       "Duplicate" is a standard button on every real Odoo form.
10. [ ] **`post_init_hook`/`uninstall_hook`** (`odoo/modules/loading.py`) —
       `IOdooAddon` has no lifecycle callback beyond `RegisterModels` for
       one-time data seeding/migration on install or cleanup on uninstall.
11. [ ] **`any`/`not any` domain operator** — relational sub-domain
       matching, e.g. `[('invoice_line_ids','any',[('price_unit','>',100)])]`.
       Depends on #3 (domain tree) landing first. Lower priority than the
       above; genuinely needs a related-model subquery-style evaluation.

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
