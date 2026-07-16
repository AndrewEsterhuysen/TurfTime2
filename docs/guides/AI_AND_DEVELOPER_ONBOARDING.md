# AI and developer onboarding — freemium rules

How humans and AI tools are required to pick up freemium architecture rules before changing Turf Time.

## Source of truth (in repo)

| File | Audience |
|------|----------|
| [`docs/FREEMIUM_RULES.md`](../FREEMIUM_RULES.md) | Full mandatory rules (R1–R24) |
| [`docs/FEATURES.md`](../FEATURES.md) | Feature catalog / free vs candidate paid |
| [`AGENTS.md`](../../AGENTS.md) | All coding agents (primary entry) |
| [`CLAUDE.md`](../../CLAUDE.md) | Claude Code / Claude-compatible agents |
| [`.aiassistant/rules/00-freemium-always.md`](../../.aiassistant/rules/00-freemium-always.md) | JetBrains AI Assistant chat |
| [`.github/copilot-instructions.md`](../../.github/copilot-instructions.md) | GitHub Copilot |

---

## Humans

1. Read `docs/FREEMIUM_RULES.md` and `docs/FEATURES.md` once when joining the project.  
2. Use the PR checklist in freemium rules for storage/teams/cloud changes.  
3. Update `docs/FEATURES.md` when adding capabilities.

---

## JetBrains Rider — AI Assistant chat (important)

Repo files under `.aiassistant/rules/` are picked up by AI Assistant, but **you must set the rule type to Always** once per machine/project so every chat includes them.

### One-time setup

1. Open the project in Rider.  
2. Go to **Settings → Tools → AI Assistant → Rules**.  
3. Confirm `00-freemium-always` (or `.aiassistant/rules/00-freemium-always.md`) is listed.  
4. Set **Rule type** to **Always**.  
5. Apply / OK.

### Verify

Start a new AI chat and ask something like “What freemium rules apply?”  
Expand attachments on the reply; the project rule should appear as attached.

### Self-review (optional)

**Settings → Tools → AI Assistant → Project Settings → Path to rules for AI Self-Review**  
Point at `docs/FREEMIUM_RULES.md` so AI code review also uses the same rules.

Official docs: [Configure project rules](https://www.jetbrains.com/help/ai-assistant/configure-project-rules.html), [Agent instructions](https://www.jetbrains.com/help/ai-assistant/configure-agent-behavior.html).

---

## JetBrains coding agents (Junie / agent mode)

Agents that honor **`AGENTS.md`** will load root `AGENTS.md` automatically. That file mandates freemium docs first.

If you use Claude Agent in the JetBrains ecosystem, **`CLAUDE.md`** points at the same rules.

---

## Other tools

| Tool | Mechanism |
|------|-----------|
| Claude Code | `CLAUDE.md` → `AGENTS.md` |
| Cursor | Root `AGENTS.md` (and/or copy rules into `.cursor/rules` if you use Cursor heavily) |
| GitHub Copilot (IDE) | `.github/copilot-instructions.md` |
| Grok Build / similar | `AGENTS.md` |

---

## What “always consider first” can and cannot guarantee

| Guarantee | How |
|-----------|-----|
| Rules live in git and travel with the repo | Yes — files above |
| JetBrains AI **chat** attaches rules every session | Yes — **if** Rule type = **Always** |
| Agents that auto-load `AGENTS.md` | Yes for tools that support it |
| Agents **challenge** non-standard approaches (R0) | Required by rules; still review answers |
| 100% obedience from every model | No — rules are strong guidance; humans still review |

For critical PRs, keep the freemium checklist in human review.

---

## Expected AI behavior: challenge and alternatives (R0)

AI should **not** only execute instructions. For architecture, product packaging, monetization, and major design:

1. Restate the outcome.  
2. Question weak assumptions.  
3. Offer industry-standard or lower-risk alternatives when they differ from the ask.  
4. Recommend a default; implement the owner’s override only after that discussion.

**Example:** If asked to build free + paid dual store apps, AI should push single-app freemium first—the industry default—before scaffolding two listings.

---

## Quick reminder prompt (optional)

If a chat session seems to ignore structure, paste:

> Follow AGENTS.md and docs/FREEMIUM_RULES.md (including R0). Challenge my approach and offer industry-standard alternatives when better. One freemium-ready app, everything free today; no second store app; no Firebase in Pages/ViewModels.
