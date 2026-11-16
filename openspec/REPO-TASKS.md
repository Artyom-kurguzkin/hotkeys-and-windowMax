<!-- repo-tasks-guide v8 plans=DevResources/plans -->
# Repo Tasks: conventions for agents

This repository's `openspec/` folder is shown and edited by the **Repo Tasks** app, a board of work items.
Every folder under `openspec/changes/` is a work item; a feature is a capability under `openspec/specs/`.
The app writes this file and refreshes it when it ships a newer version or the user moves the plans folder, so keep your own notes elsewhere.

## 0. How to take on a task that is more than a quick fix

1. **Research** how similar problems are solved in the industry before designing; note the sources.
2. **Write a plan** in `DevResources/plans/<title-slug>-<yyyy-MM-dd>.md`: why, what others do, decisions, the chunks below, how it will be verified. Link it from the work item with `x-rtm.plan: DevResources/plans/<file>.md`; the app opens it from the item page.
3. **Split** the work into discrete chunks that can each be tested on their own (a unit test and a black-box check), as subtasks in `tasks.md` or as dependent work items (`x-rtm.dependsOn`).
4. **Delegate** the chunks to cheaper models, each in its own git worktree so they never edit the same files. Keep the design decisions in the coordinating session. **Poll them** every few minutes (the worktree's `git status`) and ask a silent one for its status: completion notices get lost.
5. When they come back, **verify** each chunk, merge it into the current branch and run the whole test suite for integration.
6. **Update the board** (section 2) as you go, and leave the result **uncommitted** in the working tree for the user's review. Never commit unless the user asked.

## 1. Sign what you add or change (always)

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
  agentBy: Claude
  agentAt: 2026-11-05T17:00:00
  links: [wi12, n9]
  commits: [a1b2c3d]
  ---
  # 5 Nov — what changed

  One or two sentences a person would understand without reading the diff.
  ```

- **Linked tests**: `x-rtm.tests: [{suite: <test class short name>, test: "<test display name>"}]` on one line. Names must match the latest results file the app reads (`x-rtm.testResults` in `openspec/config.yaml`); rerun the tests so the file is fresh.
- **Linked commits**: `x-rtm.commits: [<7-char hash>, …]` for commits that do not mention `#wi<id>` in their message (those are linked by themselves).
- **Plan**: `x-rtm.plan: <path>` when there is one (section 0); keep its status line current.
- **Attachments** on a note: `attachments: [<path or URL>, …]` in its frontmatter. Files go under `local/task-tracking/wi<item>/n<note>/`; `local/` is gitignored (the app adds it), so put nothing there that others need. Links are plain `https://` URLs.
- **Subtasks**: tick what you finished (`- [x]`), add what you found out is still needed (signed, see 1).
- **Spec and tasks text** of those items, where you changed the behaviour they describe.
- **Status** and **dates**: set `status` to where the work is; `x-rtm.started: yyyy-MM-dd` is the day work began (the app stamps it on the first move to In Progress), `x-rtm.due: yyyy-MM-dd` the due day.

## 3. Create a work item

**Work item or subtask?** A work item is one result that can be finished and archived on its own; it is one card on the board. A subtask is a step towards that result. So when you are asked to add tasks, or your plan has several milestones or deliverables, each one is its own work item, not a group of subtasks inside one item: an item with more than about ten subtasks should be several items. An item that has to wait for another lists it, `dependsOn: [<id>]`, and has `status: Blocked`.

A work item is one folder, `openspec/changes/<folder>/`, holding the files of the example below. Write them yourself, step by step; do not copy another item's folder.

1. **Folder name**: a short kebab-case name from the title (`rate-limit-the-api`). It must be new: not a folder under `openspec/changes/`, and not one under `openspec/changes/archive/` either (ignore the `yyyy-MM-dd-` prefix there). If it is taken, add `-2`.
2. **Number**: `x-rtm.id` is the highest `id` in every `.openspec.yaml` under `openspec/changes/`, archived ones included, plus 1. Read them when you write the file, not earlier (section 5).
3. **Feature**: one per item, the capability the work belongs to. The features that exist are the folders under `openspec/specs/` (`features: [billing]`, nested: `[functional/banking]`) and the names under `x-rtm.features` in `openspec/config.yaml` (features with no spec yet). Use one of those when it fits. The item's delta spec (step 5) goes under that same name, `specs/<feature>/spec.md`, never under another one. Make up a new kebab-case name only for a capability that is really new, and use it in both places.
4. **Status**: `Backlog` or `Planned` for work not started; `In Progress` with `started: <today>` when you are doing it now. `label` is `bug`, `feature`, `enhancement`, or left out.
5. **Specs or not**: when the work changes what the product does, write a delta spec (below). When it does not (a refactoring, a chore), write no `specs/` folder and put `skip_specs: true` on the line after `schema:`; without one of the two the item is invalid.
6. **Links**: what the item points to is a key in its `x-rtm:` block, each on one line. Leave out what you do not have.
   - `plan: DevResources/plans/<title-slug>-<yyyy-MM-dd>.md`: the plan you wrote for the item (section 0), as a path from the repository root. Write the file first; the item page opens it.
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
- Statuses: Backlog, Planned, Confirmed, In Progress, Blocked, In Review, Completed. `Completed` means archived: archive with the OpenSpec CLI (`openspec archive <change-name> --yes`), never set it by hand.
- Keep `x-rtm` keys, comments and formatting you did not change. Lists stay on one line: `features: [a, b]`. A work item has one feature.
- Subtasks keep their `1.1`, `1.2`… numbers; add new ones at the end of their group.

## 5. Numbers

- A work item's `x-rtm.id` is unique: use the highest `id` across **all** `.openspec.yaml` files, archived ones included, plus 1. The user creates and merges items at the same time, so compute it when you write, not earlier.
- Refer to work items as `#wi<id>` and notes as `#n<id>`.
