# Odoo 19 core (D:\Practice\custom\odoo-19.0\odoo) — continuous port backlog

Source: 537 .py files, ~177k lines (`odoo/` core + `odoo/addons/` 25 bundled
modules, 111MB). Porting every file 1:1 is not the goal — much of it is
Postgres wire-protocol code, WSGI/HTTP server internals, CLI tooling, test
suites, and upgrade scripts that don't apply to a SQLite-backed .NET clone.
This file tracks portable **behaviors**, prioritized by value, worked
through continuously across turns until told to stop.

Convention: each item names the real Odoo source it's modeled on, what gets
built here, and any deliberate simplification (ponytail-style, noted inline
in code too).

## Done

- [x] RPC dispatch guard (`odoo/service/model.py` `get_public_method`) —
      `OdooModel.ExecuteMethod` now blocks framework hooks / inherited
      members, only model-declared actions are RPC-callable.
- [x] ORM domain operators (`odoo/osv/expression.py` semantics) — added
      `>=`, `<=`, `like`, `not like`, `not ilike`, `in`, `not in` to
      `ModelRegistry.SearchRead`.
- [x] Module dependency graph (`odoo/addons/base/models/ir_module.py`
      `button_immediate_install`/uninstall) — `SetModuleState` cascades
      installs through `Depends` and blocks uninstalling a module other
      installed modules still depend on.
- [x] Apps/module manager UI (`odoo/addons/base/views/ir_module_views.xml`)
      — search, Apps/Extra + Installed filters, category grouping, author/
      website/version/dependency display.
- [x] QWeb templating (`odoo/addons/base/models/ir_qweb.py` subset) —
      `t-esc`/`t-out`/`t-field`, `t-if`/`t-elif`/`t-else`, `t-foreach`/
      `t-as`, `t-att-*`, wired to a `/web/report/{model}/{template}/{id}`
      endpoint.

## In progress / next up (priority order)

1. [ ] **`ir.model.access`** (`odoo/addons/base/models/ir_model.py`
       `IrModelAccess`) — model-level CRUD permission per group
       (perm_read/write/create/unlink). Replaces the hardcoded
       `AdminOnlyModels` hash set in `UniversalRpcController` with real,
       data-driven access control.
2. [ ] **`ir.rule`** (`odoo/addons/base/models/ir_rule.py`) — record-level
       access rules: a domain per (model, group) that gets AND-ed into
       every `SearchRead`/`Write`/`Unlink` for users in that group (e.g.
       "salespeople only see their own orders"). Builds directly on the
       domain-operator work already done.
3. [ ] **`ir.sequence`** (`odoo/addons/base/models/ir_sequence.py`) —
       auto-numbering for records (e.g. `INV/2026/0001` instead of the
       user typing invoice numbers by hand in InvoicingAddon).
4. [ ] **`ir.cron`** (`odoo/addons/base/models/ir_cron.py`) — scheduled
       server actions (a minimal in-process timer running due jobs; no
       need to replicate Odoo's multi-worker cron locking).
5. [ ] **`ir.attachment`** (`odoo/addons/base/models/ir_attachment.py`) —
       file upload/attach to any record, surfaced in the Chatter.
6. [ ] **`res.lang` / translations** (`odoo/addons/base/models/res_lang.py`,
       `ir_translation`-style) — only if a concrete multi-language need
       comes up; skip until then (YAGNI today).

## Explicitly out of scope

- `D:\Practice\custom\odoo-19.0\odoo\addons\` (all 25 bundled modules) —
  skipped for now per explicit instruction. This backlog covers
  `odoo/orm/`, `odoo/osv/`, `odoo/api/`, `odoo/service/`, `odoo/modules/`
  only, i.e. framework internals, not addon-defined models/views.
- `sql_db.py` / psycopg2 wire-protocol plumbing, connection pooling — our
  `DatabaseAdapter` already abstracts Postgres/SqlServer/MySql/Sqlite via
  Dapper. Note: Postgres itself is not ruled out — `DatabaseProvider.PostgreSql`
  already exists and is fair game if a feature genuinely needs Postgres-only
  semantics to work correctly (e.g. something with no SQLite equivalent).
  Only the raw wire-protocol/cursor-pool code is skipped, not the provider.
- `http.py` (113KB WSGI server, session/router internals) — ASP.NET Core
  already is our HTTP layer.
- CLI (`cli/`), test suites (`tests/`), upgrade scripts (`upgrade/`,
  `upgrade_code/`) — not applicable to this project's shape.
