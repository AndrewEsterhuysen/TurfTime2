# UI Refresh (branch `ui-refresh`)

Branched from `master` @ c4b851f (= TestFlight 3.4.2 build 44).
Purpose: develop and test the "Pitch & Volt" visual refresh on a limited set of pages.

## Scope — ONLY these pages may change

| Area | Files | How it's reached |
|---|---|---|
| Chat | `Pages/ChatPage.xaml`, `Pages/ChatPage.xaml.cs` (incl. the code-built reaction picker modal) | Tab `ChatPage` |
| Settings | `Pages/SettingsPage.xaml`, `Pages/SettingsPage.xaml.cs` | Tab `SettingsPage` |
| Settings › Team Details | `Pages/TeamDetailsPage.xaml(.cs)` (incl. code-built confirm popup) | `settings/teamdetails` |
| Settings › Team Details › Share QR | `Pages/QrShareModal.xaml(.cs)` | modal from TeamDetailsPage |
| Settings › Team Details › Import QR | `Pages/QrImportPage.xaml(.cs)` | modal from TeamDetailsPage |
| Settings › Reports | `Pages/ReportsPage.xaml(.cs)` | `settings/reports` |
| Settings › Timers | `Pages/TimersSettingsPage.xaml(.cs)` | `settings/timers` |
| Settings › Options | `Pages/OptionsPage.xaml(.cs)` | `settings/options` |
| Help | `Pages/HelpPage.xaml(.cs)` | Tab `HelpPage` |

New shared style resources for these pages (new files only), e.g.
`Resources/Styles/PitchVolt.xaml` (+ fonts/icons under `Resources/Fonts` if Inter / Material Symbols Rounded are added).

### Out of scope (do not modify)
GamePage, DetailsPage, SetupPage, KitPage, DutiesPage, ComingSoonPage, GoalDetailModal, WelcomePage,
RotationStylePage (not reachable from Settings navigation; only referenced by GamePage via a static event),
AppShell (tab bar), and the global `Resources/Styles/Styles.xaml` / `Colors.xaml`.

## Approach
- Apply Pitch & Volt via **page-scoped styles**: a new ResourceDictionary (`PitchVolt.xaml`) merged
  into each in-scope page's `ContentPage.Resources`, using explicit keys (`Style="{StaticResource PV.Card}"`)
  or page-local implicit styles. Do **not** edit global Styles.xaml/Colors.xaml, so out-of-scope pages are unchanged.
- Light/dark via `AppThemeBinding`. Keep the read-only Disabled contrast fix (PR #2) intact.
- Keep BindableLayout team lists on TeamDetailsPage (iOS CollectionView zero-height bug).

### Palette
| Role | Light | Dark |
|---|---|---|
| Primary | #0B6E4F | #3DDC97 |
| Primary container | #D7F2E3 | #0F3B2C |
| Accent (Volt) | #C6F432 | #C6F432 |
| Warning | #FF8A00 | #FFB547 |
| Danger | #D93025 | #FF6B5E |
| Background | #F6F8F7 | #0B1210 |
| Surface | #FFFFFF | #15201C |
| Outline | #D5DDD9 | #2A3833 |
| Text | #111816 | #E8EEEB |
| Text secondary | #56635E | #9FB0A9 |

Type: Inter, scale 56/28/22/17/15/13/11. Border cards radius 16, 8pt grid, pill buttons,
segmented controls, Material Symbols Rounded icons.

## Testing
Verify each in-scope page in light + dark on iOS and Android, Admin and read-only users.
Spot-check out-of-scope pages are visually unchanged before merging to `master`.
