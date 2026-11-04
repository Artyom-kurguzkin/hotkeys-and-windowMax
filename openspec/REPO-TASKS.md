<!-- repo-tasks-guide v7 plans=DevResources/plans -->
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

## 3. Keep OpenSpec valid

- A work item is a change folder with `.openspec.yaml` (`schema: spec-driven`, `created:`, the `x-rtm:` block), `proposal.md` with `## Why` and `## What Changes`, `tasks.md` with numbered checkboxes, and, when behaviour changes, `specs/<capability>/spec.md` deltas under `## ADDED Requirements`, `## MODIFIED Requirements`, `## REMOVED Requirements` or `## RENAMED Requirements`. Every requirement has at least one `#### Scenario:`.
- A **MODIFIED** block replaces the whole requirement when the item is archived: copy **every** scenario of the current main spec into it and change what you change. (The app puts missing scenarios back before archiving, but a stale block fails validation until then.)
- Validate before you stop: `npx -y @fission-ai/openspec@1.14.3 validate <change-name> --strict`.
- Statuses: Backlog, Planned, Confirmed, In Progress, Blocked, In Review, Completed. `Completed` means archived: archive with the OpenSpec CLI (`openspec archive <change-name> --yes`), never set it by hand.
- Keep `x-rtm` keys, comments and formatting you did not change. Lists stay on one line: `features: [a, b]`. A work item has one feature.
- Subtasks keep their `1.1`, `1.2`… numbers; add new ones at the end of their group.

## 4. Numbers

- A work item's `x-rtm.id` is unique: use the highest `id` across **all** `.openspec.yaml` files, archived ones included, plus 1. The user creates and merges items at the same time, so compute it when you write, not earlier.
- Refer to work items as `#wi<id>` and notes as `#n<id>`.
