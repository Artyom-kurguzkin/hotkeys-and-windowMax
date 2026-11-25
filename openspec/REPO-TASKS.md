<!-- repo-tasks-guide v19 plans=DevResources/plans attachments=local/task-tracking -->
# Repo Tasks: conventions for agents

This repository's `openspec/` folder is shown and edited by the **Repo Tasks** app, a board of work items.
Every folder under `openspec/changes/` is a work item; a feature is a capability under `openspec/specs/`.
The app writes this file and refreshes it when it ships a newer version or the user moves the plans or attachments folder, so keep your own notes elsewhere.

## 0. Use the `repo-tasks` tools when you have them

The Repo Tasks app serves this board to agents over MCP (Model Context Protocol). The tools allocate ids under a lock, sign what they write, check test names and run the validator, so none of the hand rules below can be got wrong. **When they are loaded they are the only way to touch the board.** Never edit anything under `openspec/` by hand while they load: create, update, note and validate through them.

**Are they loaded?** Look for the eight `board_*` tools in your tool list (a client may prefix them with the server name, `mcp__repo-tasks__board_list`), or call `board_list`. In a terminal, `claude mcp list` shows `repo-tasks` and whether it is connected. If they answer, go to "Use them" below.

**Turn them on** when they are not loaded. Add this entry to `.mcp.json` at the repository root (or merge it into the `mcpServers` you already have), then restart the session or approve the server when the client asks:

```json
{
  "mcpServers": {
    "repo-tasks": {
      "type": "stdio",
      "command": "C:/Users/artyo/Documents/5-mine/repo-task-manager/TaskManager/bin/Debug/net10.0/TaskManager.exe",
      "args": ["--mcp"],
      "env": { "RTM_AGENT": "Claude" }
    }
  }
}
```

- That path is the Repo Tasks exe **on the machine that wrote this file**; it is not the same on another machine. A `.mcp.json` that is committed can name an override: `"command": "${RTM_APP:-C:/Users/artyo/Documents/5-mine/repo-task-manager/TaskManager/bin/Debug/net10.0/TaskManager.exe}"` runs the exe that the `RTM_APP` environment variable names, and this one otherwise.
- The same in one command: `claude mcp add repo-tasks -- C:/Users/artyo/Documents/5-mine/repo-task-manager/TaskManager/bin/Debug/net10.0/TaskManager.exe --mcp` (add `--scope project` to write `.mcp.json`, and `--env RTM_AGENT=<your name>` before the name to sign as yourself).
- `RTM_AGENT` is the name the tools sign with (section 1); a tool's `agent` argument overrides it.
- The app's server runs while the app is open; the stdio entry only connects to it, so the user has to open the app first (a tool call made while it is closed returns an error saying so). The app serves agents itself, in its own process (there is no separate server to start). A client that only speaks HTTP: point it at `http://127.0.0.1:7431/mcp` while the app is open, with the header `Authorization: Bearer <token>`, where the token is the content of the file `%LOCALAPPDATA%/RepoTaskManager/token` (created on first use, yours alone: never commit or paste it).

**Use them.** Every tool takes an optional `repo` (any path inside the repository; the default is the directory you work in).

- `board_list`: the work items, filtered by status, feature, label, contributor, text, or `ready` (not blocked, not started); concise rows with id, title, status and `revision`.
- `board_get`: one item in full: description, subtasks with their numbers, notes, delta specs, linked tests (passing or failing in the latest results) and commits, what it depends on and blocks, and its `revision`.
- `board_features`: the features (capabilities under `openspec/specs/` plus the ones named in `config.yaml`) with their open items; read it before you pick the feature of a new item.
- `board_create`: a new work item (section 3 done for you): title, why, what changes, feature, status, plan, `dependsOn`, grouped subtasks, delta specs. It takes the next id under the lock, signs it, validates it and returns the id.
- `board_update`: change status, priority, label, feature, dates, plan, dependencies, contributors, description, tests, commits, subtasks (tick, add) and specs of an item.
- `board_note`: add a change-log note to an item (title, body, links, commits, attachments); it takes the next note id and signs it.
- `board_validate`: the OpenSpec validator (`--strict`) plus the app's own warnings (duplicate ids, wrong statuses, test names not in the results), for one item or every open one.

The order is `board_create` (or `board_get` for an item that exists) → `board_update` while you work → `board_note` when a piece of work lands → `board_validate` before you stop. Rules for the calls:

- Pass the `revision` that `board_get` returned into `board_update` and `board_note`; if the item changed since, the call is refused with the current revision, so read again and retry. Without one the write goes through unchecked.
- Set `In Progress` when you start and `In Review` when you are done. **Never set `Completed`**: the tools refuse it, because the user archives from the app.
- Update every open item your work touches, not only the one you were given (section 2 says which), and write `#wi<id>` / `#n<id>` links in the text.
- An error message says what to fix; fix that and call again.

**Looking through items.** When you are asked to look through the In Progress items (or any set of items), `board_get` each in detailed format and read its worklog, not only its description: the notes, decisions and events added since you last looked are part of the brief; say what is new before acting.

**Log as you go.** An item's page shows its worklog: its notes and a log of events, newest first. Status changes, links (commits, tests, `docs`, dependencies) and ticked subtasks are logged for you, under your name, whenever you use these tools, so do not write a note to say them. The supervisor and every subagent write one short `board_note` per event while the work is in progress, not one summary at the end. Two sentences each, for:

- chunk started: what and where;
- tests red: which ones;
- a blocker;
- a command you ran and what came back (the output in a code block);
- chunk finished: the `Passed!` line;
- integration done.

A subagent in a worktree passes `repo` = the main repository's path, so its entries land there and not in its copy, and its own name as `agent`. An event (`ran the suite: 525 passed`) goes in `board_log`: multi-line text up to 4,000 characters and an optional `kind` from the event standard (started, found, decision, tests-red, tests-green, verified, blocked, done; the worklog labels it), no note file and no revision, one-line answer; longer text becomes a note by the same call. `board_update`'s `note` writes the hand-off note in the same call as the status, tests and commits. `title` is optional (the first line of the body is used); link documents with `board_update`'s `docs` (repo-relative paths).

**Decisions.** Record each decision as its own decision note: `board_note` with `kind: "decision"` (by hand: `kind: decision` in the note's frontmatter). Keep it clear and brief: line one what was decided, line two why, at most a third line with what it rules out. The app emphasises decision notes in the worklog. For example:

```
board_note(id: 142, kind: "decision",
  body: "Log entries are notes, decisions get kind: decision.\nWhy: the worklog can emphasise them without parsing text.\nRules out: a title prefix.")
```

**When no tools can be loaded** (no app on this machine, a client without MCP, the server will not start), the rest of this file is the fallback: the same changes by hand, in the same files, following sections 0a to 5.

## 0a. How to take on a task that is more than a quick fix

1. **Research** how similar problems are solved in the industry before designing; note the sources.
2. **Write a plan** in `DevResources/plans/<title-slug>-<yyyy-MM-dd>.md`: why, what others do, decisions, the chunks below, how it will be verified. Each decision of the plan also goes to the worklog as a decision note (section 0), one per decision. Link it from the work item with `x-rtm.plan: DevResources/plans/<file>.md`; the app opens it from the item page.
3. **Split** the work into discrete chunks that can each be tested on their own (a unit test and a black-box check), as subtasks in `tasks.md` or as dependent work items (`x-rtm.dependsOn`).
4. **Delegate** the chunks to cheaper models, each in its own git worktree so they never edit the same files. Keep the design decisions in the coordinating session. **Poll them** every few minutes (the worktree's `git status`) and ask a silent one for its status: completion notices get lost.
5. When they come back, **verify** each chunk, merge it into the current branch and run the whole test suite for integration.
6. **Update the board** (section 2) as you go, and leave the result **uncommitted** in the working tree for the user's review. Never commit unless the user asked.

## 1. Sign what you add or change (always)

Sections 1 to 5 are the hand rules: they are only for when the `repo-tasks` tools are absent (section 0). With the tools loaded they sign, number and validate for you, and you do not edit these files.

The app flags work done by an agent until the user has looked at it. So every time you **create or change** something here, sign it with your agent name (e.g. `Claude`, `Codex`), never the user's name:

- **Work item** (a change folder): in its `.openspec.yaml`, inside the `x-rtm:` block, set
  `agentBy: <your name>` and `agentAt: <local time, yyyy-MM-ddTHH:mm:ss>`. Set `agentAt` again each time you change the item.
  Never write `reviewedAt`: only the user does, by clicking the chip in the app.
- **Note** (`notes/n<N>.md`): `by: <your name>` plus the same two keys in its frontmatter.
- **Subtask** (a `tasks.md` checkbox): end the line with `<!-- agent: <your name> -->`.
- When you **create** a work item or subtask, also add your name to its `contributors` (people and agents who did the work), e.g. `contributors: [artyom, Claude]`.
- `contributors` are who did the work, people and agents. Add yourself when you create something; do not add yourself for a small edit of someone else's item.

```yaml
x-rtm:
  id: 71
  title: Short title
  status: In Progress
  contributors: [artyom]
  plan: DevResources/plans/short-title-2026-11-05.md
  agentBy: Claude
  agentAt: 2026-11-05T17:00:00
```

## 2. Keep the board true after every piece of work

When work lands, update **every** open work item it touches: the one you worked on and any older item whose topic it advances (the app shows the user all of these). For each:

- **Change log**: add `notes/n<id>.md` in the change folder. `id` is the highest across **all** `notes/n*.md` in the repository (archived changes included) plus 1. Frontmatter, then a heading and what changed, in plain words:

  ```markdown
  ---
  id: 44
  by: Claude
  created: 2026-11-05T17:00:00
  title: 5 Nov — what changed
  agentBy: Claude
  agentAt: 2026-11-05T17:00:00
  links: [wi12, n9]
  commits: [a1b2c3d]
  ---
  # 5 Nov — what changed

  One or two sentences a person would understand without reading the diff.
  ```

  The note's title goes in `title:` (a note without one is titled by its first `# ` heading, which still works); the app edits only that key when the user retitles a note, never the body.

- **Linked tests**: `x-rtm.tests: [{suite: <test class short name>, test: "<test display name>"}]` on one line. Names must match the latest results file the app reads (`x-rtm.testResults` in `openspec/config.yaml`); rerun the tests so the file is fresh.
- **Linked commits**: `x-rtm.commits: [<7-char hash>, …]` for commits that do not mention `#wi<id>` in their message (those are linked by themselves).
- **Plan**: `x-rtm.plan: <path>` when there is one (section 0a); keep its status line current.
- **Attachments** on a note: `attachments: [<path or URL>, …]` in its frontmatter. Files go under `local/task-tracking/wi<item>/n<note>/`; that folder is gitignored (the app adds it), so put nothing there that others need. Links are plain `https://` URLs.
- **Subtasks**: tick what you finished (`- [x]`), add what you found out is still needed (signed, see 1).
- **Spec and tasks text** of those items, where you changed the behaviour they describe.
- **Status** and **dates**: set `status` to where the work is; `x-rtm.started: yyyy-MM-dd` is the day work began (the app stamps it on the first move to In Progress), `x-rtm.due: yyyy-MM-dd` the due day, `x-rtm.moved: yyyy-MM-dd` the day it last moved to its status (the app stamps it on creation and on every status change; set it yourself only when you change `status` in the file).

### Keep the worklog current by hand

The `board_*` tools do this for you; when you edit files, you do it yourself. The worklog of an item is its notes plus `events.log` in the change folder (`openspec/changes/<folder>/events.log`, beside `.openspec.yaml`): one line per thing that happened, appended in time order. `events.log` must be tracked by git (commit it with the change): the app adds `!openspec/**/events.log` to `.gitignore` when a rule such as `*.log` would ignore it.

```
yyyy-MM-ddTHH:mm:ss | <who> | <kind> | <text>
2026-11-09T10:15:00 | Claude | status | Planned -> In Progress
2026-11-09T10:40:00 | Claude | subtask-done | 1.1 Count requests per API key
```

- **Append, never rewrite**: add your lines at the end, keep every line that is there, and keep the order by time. `<who>` is your agent name (section 1), never the user's. Local time, to the second; one line each, no ` | ` in the text.
- Log what you change, with these kinds and texts:
  - `created`: the title (when you create the item).
  - `status`: `Old -> New`, e.g. `Planned -> In Progress`.
  - `commit-linked` / `commit-unlinked`: the commit hash, when you add or remove it in `x-rtm.commits`.
  - `test-linked` / `test-unlinked`: `Suite / Test`, when you add or remove it in `x-rtm.tests`.
  - `doc-linked` / `doc-unlinked`: the repo-relative path, when you add or remove it in `x-rtm.docs`.
  - `subtask-done` / `subtask-reopened` / `subtask-added`: `<number> <text>`, e.g. `1.2 Refuse the 101st`; `subtask-added` also for subtasks written at creation.
  - `dependency-added` / `dependency-removed`: `#wi<id>` of the item it waits for.
  - `title`: `Old -> New`, for a rename. `priority`, `label`: `Old -> New`. `dates`: what changed, e.g. `due 2026-11-20`.
  - `feature`: `old -> new`.
  - `contributor-added` / `contributor-removed`: the name.
  - `plan-linked` / `plan-unlinked`: the repo-relative path of the plan.
  - `description-edited`: `proposal.md`.
  - `attachment-added`: `n<note>: <file>`, e.g. `n44: screenshot.png`.
  - `merged-from`: on the item that survives a merge, one line per item merged into it, `#wi<id> <its title>`.
- Linked documents are `x-rtm.docs: [<path>, …]` in the `x-rtm:` block: repo-relative paths, on one line.
- What the log cannot say, a command you ran and what came back, a decision, a blocker, the result of a chunk of work, goes in a note (section 2), one short note per event, as you go.

## 3. Create a work item

**Work item or subtask?** A work item is one result that can be finished and archived on its own; it is one card on the board. A subtask is a step towards that result. So when you are asked to add tasks, or your plan has several milestones or deliverables, each one is its own work item, not a group of subtasks inside one item: an item with more than about ten subtasks should be several items. An item that has to wait for another lists it, `dependsOn: [<id>]`, and has `status: Blocked`.

A work item is one folder, `openspec/changes/<folder>/`, holding the files of the example below. Write them yourself, step by step; do not copy another item's folder.

1. **Folder name**: a short kebab-case name from the title (`rate-limit-the-api`). It must be new: not a folder under `openspec/changes/`, and not one under `openspec/changes/archive/` either (ignore the `yyyy-MM-dd-` prefix there). If it is taken, add `-2`.
2. **Number**: `x-rtm.id` is the highest `id` in every `.openspec.yaml` under `openspec/changes/`, archived ones included, plus 1. Read them when you write the file, not earlier (section 5).
3. **Feature**: one per item, the capability the work belongs to. The features that exist are the folders under `openspec/specs/` (`features: [billing]`, nested: `[functional/banking]`) and the names under `x-rtm.features` in `openspec/config.yaml` (features with no spec yet). Use one of those when it fits. The item's delta spec (step 5) goes under that same name, `specs/<feature>/spec.md`, never under another one. Make up a new kebab-case name only for a capability that is really new, and use it in both places.
4. **Status**: `Backlog` or `Planned` for work not started; `In Progress` with `started: <today>` when you are doing it now. `label` is `bug`, `feature`, `enhancement`, or left out.
5. **Specs or not**: when the work changes what the product does, write a delta spec (below). When it does not (a refactoring, a chore), write no `specs/` folder and put `skip_specs: true` on the line after `schema:`; without one of the two the item is invalid.
6. **Links**: what the item points to is a key in its `x-rtm:` block, each on one line. Leave out what you do not have.
   - `plan: DevResources/plans/<title-slug>-<yyyy-MM-dd>.md`: the plan you wrote for the item (section 0a), as a path from the repository root. Write the file first; the item page opens it.
   - `dependsOn: [<id>, …]`: the items this one waits for.
   - `tests: [{suite: <test class>, test: "<test display name>"}]`: the tests that prove the work, and `commits: [<7-char hash>, …]`: the commits that made it (section 2 has the rules for both).
   - `started: yyyy-MM-dd` and `due: yyyy-MM-dd`.
   - In any text of the item, write `#wi<id>` for another item and `#n<id>` for a note.
   - Anything longer, and files and web links, go in a note of the item, `notes/n<id>.md`, with `links:`, `commits:` and `attachments:` in its frontmatter (section 2).
7. **Sign it** (section 1) and **validate** it (section 4) before you stop.

A complete, valid item:

`openspec/changes/rate-limit-the-api/.openspec.yaml`

```yaml
schema: spec-driven
created: 2026-11-06
x-rtm:
  id: 98
  title: Rate-limit the API
  status: Planned
  contributors: [Claude]
  features: [api-limits]
  label: feature
  plan: DevResources/plans/rate-limit-the-api-2026-11-06.md
  agentBy: Claude
  agentAt: 2026-11-06T10:00:00
```

`openspec/changes/rate-limit-the-api/proposal.md`

```markdown
## Why

One client can flood the API and slow it down for everyone else; there is no limit on requests today.

## What Changes

- Each API key gets 100 requests a minute; more are refused until the minute is over.
```

`openspec/changes/rate-limit-the-api/tasks.md`

```markdown
## 1. Limiter

- [ ] 1.1 Count requests per API key and refuse the 101st <!-- agent: Claude -->
```

`openspec/changes/rate-limit-the-api/specs/api-limits/spec.md`

```markdown
## ADDED Requirements

### Requirement: Requests are limited per API key
The API SHALL refuse a key's requests beyond 100 in one minute with status 429.

#### Scenario: Limit reached
- **WHEN** a key sends its 101st request within a minute
- **THEN** the response is 429 and the request is not processed
```

`## Why` is 50 to 1000 characters. In `tasks.md` every subtask sits under a group heading, `## <group>. <name>`, and is numbered `<group>.<n>`: `- [ ] 1.1 …`, `- [ ] 1.2 …`, then `## 2. …` with `- [ ] 2.1 …`. A list with no group heading, or numbered `1.`, `2.`, shows on the board without its groups and numbers. To add a subtask to an item that exists, append it to its group with the next number and sign it; never renumber the others. A delta spec is always `specs/<capability>/spec.md`, one per capability, never `specs/spec.md`. Every requirement is a sentence or two containing SHALL or MUST (under 500 characters), then at least one `#### Scenario:` with `- **WHEN** …` and `- **THEN** …` lines.

**Which heading a requirement goes under.** Open the main spec, `openspec/specs/<capability>/spec.md`, and look for `### Requirement: <the exact name>`. Only the main spec counts, not what another open change adds.

- The capability has no spec file yet, or the requirement is not in it: `## ADDED Requirements`. A new capability's delta has ADDED requirements only.
- The requirement is there and you change it: `## MODIFIED Requirements`, under its exact name, with the whole requirement: its text and **every** scenario, changed where you change it. The block replaces the old one when the item is archived.
- It is there and you delete it: `## REMOVED Requirements` with its `### Requirement: <name>` line alone.
- It is there and you rename it: `## RENAMED Requirements` with `- FROM: ### Requirement: <old name>` and, on the next line, `- TO: ### Requirement: <new name>`.

## 4. Keep OpenSpec valid

- Validate before you stop: `npx -y @fission-ai/openspec@1.14.3 validate <change-name> --strict`.
- A wrong heading (section 3) or a MODIFIED block that lacks a scenario the main spec has since gained makes the item invalid. The app repairs both when the user completes the item, but until then every reader of the item sees it wrong: get it right when you write it.
- Statuses: Backlog, Planned, Confirmed, In Progress, Blocked, In Review, Completed unless the project lists its own under `x-rtm.statuses` in `openspec/config.yaml` (Backlog, In Progress and Completed are always there; the tools' error names the project's list). `Completed` means archived: archive with the OpenSpec CLI (`openspec archive <change-name> --yes`), never set it by hand.
- Keep `x-rtm` keys, comments and formatting you did not change. Lists stay on one line: `features: [a, b]`. A work item has one feature.
- Quote a value that contains `:` or `#`, or that starts with a special character (`-`, `*`, `&`, `!`, `[`, `{`, `"`, `'`, `%`, `@`), or that is `yes`, `no`, `null` or a number: `title: "Fix: the thing"`. An unquoted one is not valid YAML; the app reads the common cases as text and quotes them on its next save, but the OpenSpec CLI does not.
- Subtasks keep their `1.1`, `1.2`… numbers; add new ones at the end of their group.

## 5. Numbers

- A work item's `x-rtm.id` is unique: use the highest `id` across **all** `.openspec.yaml` files, archived ones included, plus 1. The user creates and merges items at the same time, so compute it when you write, not earlier.
- Refer to work items as `#wi<id>` and notes as `#n<id>`.
