# Headroom

![platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078d4)
![framework](https://img.shields.io/badge/.NET-10-512bd4)
![dependencies](https://img.shields.io/badge/NuGet%20packages-0-1baf7a)
![license](https://img.shields.io/badge/license-MIT-blue)

**A Windows tray app that always knows how much Claude Code and Codex you have left.**

Live "% left" meters for every rate-limit window on every account, reset
countdowns, threshold alerts, and local usage history — in one tray icon.

- **One process.** No daemon, no localhost port, no database server.
- **Zero NuGet packages.** `dotnet build` works offline on a clean machine.
- **Local only.** No cloud, no telemetry, no account. Nothing leaves your PC
  except the two calls the providers already expect.
- **Never touches your credentials.** Headroom reads what the CLIs already
  stored. It never writes a credential, never refreshes a token, never signs
  you in, and never signs you out.

---

## Why it exists

macOS has [ModelDeck](https://github.com/timharris707/modeldeck). Windows had
nothing. Headroom is a from-scratch Windows app built on the same idea, sharing
none of its code — and its architecture is deliberately different, because the
constraints are.

ModelDeck splits into a SwiftUI menu-bar app plus a Node daemon under `launchd`,
with a token-guarded localhost API between them. That split exists because a
sandboxed macOS app is a poor place to shell out to CLIs and own a database. A
Windows tray app has no such problem — and it is already the always-running
process — so Headroom folds the whole thing into one executable. Gone with the
split: the port to secure, the IPC layer, the second crash surface, the second
update path, and the version-skew problem between them.

The one thing worth copying wholesale was not the architecture. It was the
**honesty about state**, and it is the design spine of this app.

---

## The rules this app is built on

**Never show a number you can't stand behind.**

- A failed refresh keeps the last good numbers *and their original timestamp*,
  then says why they're old. It doesn't blank the card, and it doesn't quietly
  pass stale data off as fresh.
- An expired token reads **"Idle — renews on next use"**, not "signed out". It
  is not a sign-out: the CLI renews it the next time you use that account, and
  sending you to a needless `/login` is a worse error than saying nothing.
- Only a *structured* `authentication_error` is treated as signed out. A bare
  401 is transient.
- Staleness goes amber **only when nothing else explains it**. An idle account
  holding week-old numbers is behaving correctly; painting that amber teaches
  you to ignore amber, and then it can't warn you on the day a refresh really is
  failing silently. The footer says `Live accounts current · 2 idle` for the
  normal case and names the offender for the abnormal one.
- A payload shape Headroom doesn't recognise degrades to "unknown", never to
  "0% used". These endpoints are internal and undocumented; they will change.
- Money is never displayed unless the payload **states its currency**. A dollar
  sign in front of a number nobody labelled is a lie.
- Notifications fire **once, at the crossing**. A meter that re-notifies every
  poll while you sit at 9% is a meter you mute — and a muted meter can't warn
  anyone.

---

## How it reads your usage

Both providers, in one process, through the channels they already use:

| Provider | Source |
|---|---|
| **Claude** | `%USERPROFILE%\.claude\.credentials.json` → `GET https://api.anthropic.com/api/oauth/usage` |
| **Codex** | `codex app-server --stdio` → three lines of JSON-RPC → `account/rateLimits/read` |

On Windows and Linux, Claude Code stores its OAuth token as plain JSON in the
profile folder — which is why Headroom needs no keychain interop at all. The
Codex path uses the CLI's own documented protocol rather than scraping output,
so the numbers come from exactly where the CLI gets them.

Requests identify as `claude-code/<version>` because a generic user agent lands
that endpoint in a stricter rate-limit bucket.

### Multiple accounts

Each account points at a profile folder that the CLI already owns —
`CLAUDE_CONFIG_DIR` for Claude, `CODEX_HOME` for Codex. There is no symlinking,
no swapping of the CLI's real home, and no copying of credentials, which is what
makes it structurally impossible for Headroom to lose a sign-in.

On first launch it adopts whatever is already on the machine (`~/.claude`,
`~/.codex`, and any `~/.claude-profiles/*` / `~/.codex-profiles/*`), so it shows
real numbers immediately instead of an empty setup screen.

To add a second account, point the CLI at a new folder once:

```powershell
# PowerShell — sign a second Claude account into its own profile folder
$env:CLAUDE_CONFIG_DIR = "$HOME\.claude-profiles\work"
claude   # then /login

# and a second Codex account
$env:CODEX_HOME = "$HOME\.codex-profiles\work"
codex login
```

Then add that folder in **Settings → Accounts**.

---

## Build and run

**Requirements:** Windows 10 1809+ (Windows 11 for rounded corners and the Mica
backdrop) and the [.NET 10 SDK](https://dotnet.microsoft.com/download). The
Claude Code and/or Codex CLIs for whichever providers you want to track.

```powershell
git clone <this repo>
cd Headroom

dotnet build                                    # no restore of third-party packages
dotnet run --project tests\Headroom.Core.Tests  # 103 tests, no test framework needed
dotnet run --project src\Headroom.App           # launches to the tray
```

For a real install:

```powershell
dotnet publish src\Headroom.App -c Release -r win-x64 --self-contained false
```

Then tick **Start Headroom when I sign in** in Settings — it writes a per-user
`Run` key entry you can see and remove from Task Manager's Startup tab like any
other app.

---

## What it looks like

**The tray icon changes role rather than growing.** A macOS menu bar can put a
glyph and a percentage side by side; a Windows tray slot is a small square. So
while everything is healthy the icon is a quiet three-bar glyph. The moment
something crosses a threshold, the number itself becomes the icon, in its status
colour, with a thin fraction bar underneath — legible at 16px with no text
beside it.

**The deck** is a frameless popover anchored to the notification area, on the
right monitor, at the correct corner for wherever your taskbar lives. One card
per account: worst-window meter, plan tier, next reset. Click a card to expand
every window — 5-hour, weekly, and model-scoped caps like "Opus weekly" — each
with its own meter and reset time.

**The history window** plots capacity remaining over time, with dashed guides at
your warning and critical thresholds, a crosshair tooltip, direct labels on every
line, and a table view of the same data.

---

## Design notes

**Colour.** Light and dark are two selected palettes sharing every resource key,
not one palette inverted — dark mode swaps a single merged dictionary and the
whole app follows. Chart series use a fixed categorical order assigned by the
window's identity, never by its rank in the current filter, so toggling one
series never repaints the others. Status colours (warning, critical) are
reserved and never themed, and never carry meaning alone: every meter ships with
its window label and its "% left" number beside it.

**History storage is JSON Lines, not SQLite.** The whole dataset is a few
accounts × a few windows × a poll every five minutes — kilobytes a day.
JSON Lines gives durable appends, survives a torn write (a partial last line is
skipped on read), can be inspected in Notepad, backed up by copying, and pruned
by deleting a file. SQLite would improve none of that and would add a native
dependency to an app that currently has none.

**Backoff.** Failed probes back off exponentially with ±20% jitter, capped at 30
minutes, and a provider-stated `Retry-After` always wins when it's longer.
Pressing **Refresh** ignores the gate — an explicit human request always gets an
attempt. A signed-out account is *not* put on a backoff curve, because nothing
about it will change until you sign in.

**Layout.**

```
src/Headroom.Core/     Providers, parsing, storage, scheduling, alerts. No UI.
src/Headroom.App/      WPF tray shell. Views, view models, custom controls.
tests/                 103 tests. No test framework, no packages.
```

`Headroom.Core` has no UI dependency of any kind, and the view models depend
only on `System.ComponentModel` — both layers compile and run without WPF, which
is how the test suite covers the logic without a UI harness.

---

## Privacy

|  |  |
|---|---|
| Cloud services | **None.** No backend, no sync, no account. |
| Telemetry | **None.** |
| Your credentials | **Read in place, never copied, never written, never logged.** |
| Network | Two destinations: `api.anthropic.com` for Claude usage, and whatever the Codex CLI itself contacts. Nothing else. |
| Stored data | Settings and usage history under `%LOCALAPPDATA%\Headroom`. Percentages and timestamps only — no tokens, no prompts, no transcripts. |
| Uninstall | Delete the app, then delete `%LOCALAPPDATA%\Headroom`. Your `.claude` and `.codex` folders are untouched. |

---

## Caveats worth knowing

- **`/api/oauth/usage` is an internal endpoint**, not a documented public API. It
  can change shape or disappear without notice. Headroom degrades to "unknown"
  rather than crashing when it does, but that is the one real fragility here.
- **History starts when Headroom does.** There is no backfill of usage from
  before you installed it.
- **Windows 10 gets square corners and no Mica.** Every window paints its own
  background, so it looks intentional rather than broken — but the backdrop
  effects are Windows 11 only.

---

## License

MIT — see [LICENSE](LICENSE).

Headroom shares no code with [ModelDeck](https://github.com/timharris707/modeldeck),
the macOS app that inspired it. What it does borrow is an idea: that a usage meter
is only worth having if it refuses to show you a number it can't stand behind.
