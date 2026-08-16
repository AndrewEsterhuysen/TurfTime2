# Admin controller & Watch Only — rules and workflow

How shared-team **Admin** match control works on the Game tab: the grey **Watch Only** / **Take Control** button, the yellow view-only banner, and single-controller rules.

This document reflects the app behaviour implemented in `GameViewModel`, `GamePage`, and related cloud roster control fields. It is user-facing product knowledge for support and design — not freemium policy (see `docs/FREEMIUM_RULES.md`).

---

## Two different UI pieces (easy to mix up)

On shared teams, Game has **two** related controls:

| UI | When visible | What it is |
|----|----------------|------------|
| **Grey button** “Watch Only” / “Take Control” | Setup or Finished, **no** match controller yet, **you are cloud Admin** | Voluntary choice on **this phone only** |
| **Yellow banner** | Whenever this device is treated as “view-only” (`IsMember` effective) | Status strip; sometimes tappable for control handoff |

“Watch Only” text appears on:

1. The **grey button** (before anyone starts), and  
2. The **yellow banner** *after* you opt into Watch Only:  
   `WATCH ONLY (this device) — another Admin can run the game`

---

## Purpose of Watch Only

**Problem:** Several Admins can open Game on different phones. Only **one** may drive a live match (Start, timers, rotate, scores, roster edits). Two writers fighting over Firestore would desync the game.

**Watch Only** is an **optional, device-local** mode so an Admin can say:

> “I will only **follow** the match on this phone; another Admin should run it.”

Important properties:

- Does **not** change your cloud role (you stay Admin/Owner).
- Does **not** demote you to Member in Firebase.
- Stored as `session_view_only_{teamId}` in **Preferences on this device**.
- Only offered when **no one** holds match control and phase is **Setup** or **Finished** (`CanUseSessionViewOnly`).

So the point is **multi-device hygiene before kickoff**, not a permanent role change.

---

## Workflow around the grey “Watch Only” button

### Before the game starts (Setup / Finished, no controller)

```
Cloud Admin on phone A
        │
        ├─ Does nothing → still full Admin on this device; can press Start
        │
        └─ Taps “Watch Only” (confirm dialog)
                 │
                 └─ This device becomes effective view-only
                    • Yellow banner appears
                    • Start / rotate / score controls behave like a Member
                    • Cloud mirror stays on (follow live state)
                    • Button label flips to “Take Control”
```

From `SetSessionViewOnlyAsync` / `IsMember`:

- `_sessionViewOnly = true` → `IsMember` becomes true (even though `_userRole` is still admin).
- `IsAdmin` (effective) becomes false → bottom controls / edits blocked the same way as a real Member.

### Taking control back (still setup, no one has started)

Tap **“Take Control”** on the grey button → confirm → `_sessionViewOnly = false` → full Admin UI again on this device.

You do **not** need “Request control” for that, because there is **no** live controller yet.

### First Admin to press Start

Whoever is a free Admin (not in Watch Only, not locked) presses **Start**:

1. `ClaimControllerIfNeeded()` sets them as match controller in cloud (`controllerUid` / display name).
2. That also **clears** their own Watch Only flag if set.
3. Other devices see cloud state and become **forced locked** (`_forceLockedByController`) if they are co-Admins.
4. Their yellow banner becomes something like:  
   **“{Name} started game · Request control”**  
   (not the voluntary “WATCH ONLY (this device)” string).

So: **Start claims the single control seat.** Watch Only is the optional “I won’t claim / I won’t drive” posture *before* that.

---

## Yellow banner behaviour (not the same as the grey button)

Banner visibility: `IsMember` (true member **or** session Watch Only **or** locked by another controller).

| Situation | Banner text (approx.) | Tap action |
|-----------|------------------------|------------|
| True **Member** | VIEW-ONLY MODE — Team Admin controls the game | Usually no control action |
| You chose **Watch Only** (setup) | WATCH ONLY (this device) — another Admin can run the game | Banner tap does **not** toggle Watch Only; use grey **Take Control** |
| Co-Admin, **someone else started** | {Name} started game · Request control | Tap → **Request control** (they Accept/Reject) |
| Live match, **no controller** (relinquish / stale) | No controller · Tap to take control | Tap → **Take vacant control** |
| You already requested | … request sent… | Wait |

So:

- Clicking the **yellow banner** is **not** “toggle Watch Only until game ends.”
- Clicking the **grey Watch Only button** is what opts in/out of voluntary view-only **before** control exists.
- During a **live** match, handoff is **Request control** / **Take vacant control**, not the setup Watch Only toggle (`CanUseSessionViewOnly` is false while a controller is active or phase isn’t Setup/Finished).

---

## What happens for the rest of the match

If you are in **voluntary Watch Only** and **another Admin starts**:

- You stay view-only (mirror cloud).
- Banner becomes the **“{Name} started game · Request control”** style (forced lock path).
- To drive again mid-game: **Request control** (or take vacant seat if they relinquish / go offline ~90s).
- **Game end / Reset** releases control; free Admins can Start the next game; grey Watch Only can appear again in Setup/Finished with no controller.

If **you** later take control (or start), Watch Only on your device is cleared and you run the match.

---

## Direct answers (FAQ)

**What is the purpose of this banner/button?**  
To support **one match controller** across multiple Admin phones: let an Admin **voluntarily sit out** on their device so another Admin can run the game without write clashes—**without** demoting them from Admin.

**When clicking it, does the UI become view-only until they request control or the game ends?**  

- **Grey “Watch Only”:** UI becomes view-only on **this device** until they tap **“Take Control”** (while still in setup / no controller), **or** until a live control situation takes over (someone starts → request/vacant-control rules apply).  
- It is **not** “until the game ends” as a hard rule; they can leave Watch Only earlier in setup, or request/take control during a live match via the **yellow banner**.  
- **Yellow banner** mid-game is about **Request / Take control**, not re-enabling the setup Watch Only button.

**Is it pointless?**  
Not if you often have **two Admin phones** on the sideline. It’s optional: if you’re the only Admin who will press Start, you can ignore Watch Only entirely. It exists so a second Admin phone doesn’t accidentally edit/start against the intended controller.

---

## Mental model

```
Setup (no controller)
  ├─ Grey "Watch Only"  →  I won't drive on this phone (role still Admin)
  └─ Grey "Take Control" →  I'm free to drive / Start again

Start (first free Admin)
  └─ Claims cloud "controller seat"

Live match
  ├─ Controller: full controls
  └─ Others: yellow banner → Request control / Take if vacant

Finished / Reset
  └─ Seat free again → Watch Only button can return
```

---

## Help page summary (aligned with product)

Only **one Admin at a time** may control a live match (timers, rotate, scores, roster edits). This prevents two devices from fighting over the same game.

- **Start** on a free Admin device claims control and publishes it to the cloud.
- **Yellow banner — Member:** “VIEW-ONLY MODE — Team Admin controls the game.”
- **Yellow banner — locked co-Admin:** “{Name} started game · Request control” — tap to ask the controller to hand over. They get Accept / Reject on their Game tab.
- **Vacant control:** after Relinquish or server auto-release (~90s offline), Admins see “No controller · Tap to take control.”
- **Watch Only** (grey button in Setup / Finished): voluntary view-only on this device without demoting your Admin role. Tap again for **Take Control** when no one holds the seat.
- **Relinquish Match Control** (Team Admin Panel): free the seat so another Admin can take over without ending the match.
- After a full **Reset**, control is released and any Admin may Start the next game.

---

## Code map (for maintainers)

| Concern | Primary location |
|---------|------------------|
| Effective member / admin / Watch Only flags | `ViewModels/GameViewModel.cs` — `IsMember`, `IsSessionViewOnly`, `CanUseSessionViewOnly`, `ViewOnlyBannerText` |
| Toggle Watch Only | `GameViewModel.SetSessionViewOnlyAsync` · `GamePage.OnSessionViewOnlyToggleClicked` |
| Claim controller on Start | `GameViewModel.ClaimControllerIfNeeded` |
| Yellow banner taps | `GamePage.OnViewOnlyBannerTapped` · `RequestControlAsync` / `TakeVacantControlAsync` |
| UI layout | `Pages/GamePage.xaml` — banner + `SessionViewOnlyBtn` |
| Cloud control fields | Roster snapshot: `controllerUid`, `controllerDisplayName`, control-request fields · `ICloudRosterService` |

---

*Saved from product analysis of Turf Time shared-team Admin controller / Watch Only behaviour.*
