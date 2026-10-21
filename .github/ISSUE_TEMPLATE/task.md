---
name: Task
about: A change that can be merged on its own.
title: ''
labels: 'type: task'
assignees: ''
---

<!--
Write the title and body in English. Suggested title format: [Module] Concise action.
-->

## Background and goal

Describe the symptom, what triggers it, and its impact. Include reproduction steps, failing output, and the affected endpoints, configuration, or deployment setup when you have them.

Related code/docs: `src/...`, `frontend/...`, `docs/...`

## Interfaces and behavior that must not change

- State the HTTP routes, request/response JSON, error codes, JWT claims and authorization rules, database tables, migrations, object storage paths, configuration/Consul keys, or deployment details that must stay fixed.
- If the fix has to cover several code paths, state what every path must satisfy and which shared function or module the fix belongs in.

## Scope

- What to change.
- **What is explicitly excluded**, especially constraints such as "do not break existing API compatibility", "do not rename tables", "do not refactor X", or "do not expose secrets".

## Acceptance criteria

- Successful outcomes that can be checked one by one.
- Applicable failure, security, authentication/authorization, migration, and concurrency outcomes; when one does not apply, write "Not applicable" and why.
- When the API, database, or configuration is involved, state the compatibility and rollback outcome.

## Documentation and compatibility impact

State whether the change affects the API, JSON, JWT/authorization, database/migrations, object storage, configuration, the admin console, containers, deployment, or English documentation; write "None" when nothing is affected.

## Dependencies

- GitHub native Blocked by: #NNN, or "None"
- GitHub native Blocks: #NNN, or "None"

## Delivery constraints

- When public behavior or usage changes, update the English README, docs, API documentation, and migration notes in the same change.
