<!-- repo-tasks-guide v2 -->
# Repo Tasks: conventions for agents

This repository's `openspec/` folder is shown and edited by the **Repo Tasks** app, a board of work items.
Every folder under `openspec/changes/` is a work item; a feature is a capability under `openspec/specs/`.
The app writes this file and refreshes it when it ships a newer version, so keep your own notes elsewhere.

## Mark what you add or change (always)

The app flags work done by an agent until the user has looked at it. So every time you **create or change** something here, stamp it:

- **Work item** (a change folder): in its `.openspec.yaml`, inside the `x-rtm:` block, set
  `agentBy: <your name>` and `agentAt: <local time, yyyy-MM-ddTHH:mm:ss>`. Set `agentAt` again each time you change the item.
  Never write `reviewedAt`: only the user does, by clicking the chip.
- **Note** (`notes/n<N>.md`): the same two keys in its frontmatter, plus `by: <your name>`.
- **Subtask** (a `tasks.md` checkbox): end the line with `<!-- agent: <your name> -->`.

## Dates

- `x-rtm.started: yyyy-MM-dd` is the day work started; the app stamps it the first time an item moves to In Progress. Set it yourself if you start work on an item.
- `x-rtm.due: yyyy-MM-dd` is the due day. The calendar view draws an item from `started` to `due`; the board and list group and filter by these periods.

```yaml
x-rtm:
  id: 71
  title: Short title
  status: Backlog
  agentBy: Claude
  agentAt: 2026-10-31T17:00:00
```

## Numbers

- A work item's `x-rtm.id` is unique: use the highest `id` across **all** `.openspec.yaml` files, archived ones included, plus 1.
- A note's `id` is the highest across **all** `notes/n*.md` files plus 1; its file is `n<id>.md`.
- Refer to work items as `#wi<id>` and notes as `#n<id>` (commits mentioning `#wi<id>` are linked automatically).

## Keep the files the app reads intact

- Statuses: Backlog, Planned, Confirmed, In Progress, Blocked, In Review, Completed. `Completed` means archived: archive with the OpenSpec CLI, never set it by hand.
- Keep `x-rtm` keys, comments and formatting you did not change. Lists stay on one line: `features: [a, b]`.
- Linked tests: `tests: [{suite: <test class>, test: "<display name>"}]`; linked commits: `commits: [<7-char hash>]`.
- Subtasks keep their `1.1`, `1.2`… numbers; add new ones at the end of their group.
