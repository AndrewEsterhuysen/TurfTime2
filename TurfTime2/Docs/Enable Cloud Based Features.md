# Enable Cloud Based Features

This document describes every change needed to re-enable the full cloud/Firebase feature set
(shared teams, Firestore roster sync, Chat, FCM push notifications, member view-only mode,
and the Location tab) that was intentionally hidden for the local-only release.

All code is already in place and fully functional. No logic needs to be rewritten —
only visibility flags and one initialisation guard need to be reverted.

---

## 1. `TeamDetailsPage.xaml` — Restore Team Type UI

### 1a. Show the checkbox row labels and re-enable the `LocalTeamSection` default state

Find the hidden checkbox wrappers and restore them to visible with their original labels.
Also revert `LocalTeamSection` back to `IsVisible="False"` (shown only when Local is checked)
and `CreateLocalSection` back to `IsVisible="False"` (shown only when expanded and Local selected).

**Before (local-only release):**
```xml
<!-- Hidden checkboxes — Local is always active in this release -->
<HorizontalStackLayout IsVisible="False">
	<CheckBox x:Name="SharedCheckbox" CheckedChanged="OnSharedCheckboxChanged"/>
</HorizontalStackLayout>
<HorizontalStackLayout IsVisible="False">
	<CheckBox x:Name="LocalCheckbox" IsChecked="True" CheckedChanged="OnLocalCheckboxChanged"/>
</HorizontalStackLayout>

<!-- Local Teams List -->
<StackLayout x:Name="LocalTeamSection" IsVisible="True" Spacing="10">
```

**After (cloud release):**
```xml
<HorizontalStackLayout Spacing="10">
	<CheckBox x:Name="SharedCheckbox" CheckedChanged="OnSharedCheckboxChanged"/>
	<Label Text="Shared (Cloud sync with team members)" FontSize="14" VerticalOptions="Center"/>
</HorizontalStackLayout>

<HorizontalStackLayout Spacing="10">
	<CheckBox x:Name="LocalCheckbox" CheckedChanged="OnLocalCheckboxChanged"/>
	<Label Text="Local (Device only, no cloud sync)" FontSize="14" VerticalOptions="Center"/>
</HorizontalStackLayout>

<!-- Local Teams List (visible when Local is selected) -->
<StackLayout x:Name="LocalTeamSection" IsVisible="False" Spacing="10">
```

### 1b. Restore the `CreateLocalSection` default visibility

```xml
<!-- Before -->
<StackLayout x:Name="CreateLocalSection" IsVisible="True" Spacing="10">

<!-- After -->
<StackLayout x:Name="CreateLocalSection" IsVisible="False" Spacing="10">
```

### 1c. Restore the `CreateTeamHint` text

```xml
<!-- Before -->
<Label x:Name="CreateTeamHint" Text="Tap to expand" ... />

<!-- After -->
<Label x:Name="CreateTeamHint"
	   Text="Tap to expand — most users join with an invite code above" ... />
```

### 1d. Restore the `CreateTeamNoModeLabel` visibility in XAML

```xml
<!-- Before -->
<Label x:Name="CreateTeamNoModeLabel" ... IsVisible="False" ... />

<!-- After -->
<Label x:Name="CreateTeamNoModeLabel" ... />
<!-- (no IsVisible override — controlled entirely by UpdateCreateTeamSubSections) -->
```

### 1e. Restore comments on hidden cloud sections

Change the comments on `JoinTeamSection`, `RejoinAdminSection`, and `SharedTeamSection`
back to their original descriptions (remove the "cloud-only — hidden" notes).

---

## 2. `TeamDetailsPage.xaml.cs` — Restore Checkbox and Mode Logic

### 2a. Restore `OnAppearing` to mode-aware checkbox selection

**Before (local-only release):**
```csharp
protected override void OnAppearing()
{
	base.OnAppearing();
	LoadCurrentTeam();

	// This release is local-only. Always ensure Local is selected.
	if (!LocalCheckbox.IsChecked)
		LocalCheckbox.IsChecked = true;
	else
		_ = LoadLocalTeamsAsync();
}
```

**After (cloud release):**
```csharp
protected override void OnAppearing()
{
	base.OnAppearing();
	LoadCurrentTeam();

	var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
	if (!string.IsNullOrEmpty(teamMode))
	{
		if (teamMode == "local")
			LocalCheckbox.IsChecked = true;   // triggers LoadLocalTeamsAsync
		else if (teamMode == "shared")
			SharedCheckbox.IsChecked = true;  // triggers LoadSharedTeamsAsync
	}
}
```

### 2b. Restore `UpdateCreateTeamSubSections` to mode-aware branching

**Before (local-only release):**
```csharp
private void UpdateCreateTeamSubSections()
{
	if (!_createTeamExpanded) return;
	bool isShared = SharedCheckbox.IsChecked;
	CreateTeamNoModeLabel.IsVisible = false;
	CreateSharedSection.IsVisible = isShared;   // stays false in this release
	CreateLocalSection.IsVisible = true;
}
```

**After (cloud release):**
```csharp
private void UpdateCreateTeamSubSections()
{
	if (!_createTeamExpanded) return;
	bool isShared = SharedCheckbox.IsChecked;
	bool isLocal  = LocalCheckbox.IsChecked;
	CreateTeamNoModeLabel.IsVisible = !isShared && !isLocal;
	CreateSharedSection.IsVisible   = isShared;
	CreateLocalSection.IsVisible    = isLocal;
}
```

---

## 3. `AppShell.xaml.cs` — Re-enable the Chat Tab

The Chat tab is already in `AppShell.xaml`. It is hidden at runtime by
`UpdateMenuItemAvailability` when the team is local. No XAML change is needed;
the existing logic already shows Chat for shared teams:

```csharp
// AppShell.xaml.cs — already correct, no change needed
ChatTab.IsVisible = !isLocal;  // true when team_mode == "shared"
```

This means Chat will automatically reappear as soon as a user selects or creates
a shared team. No code change required here.

---

## 4. `GamePage.xaml` — Restore the Member Read-Only Banner

The banner is already in the XAML and bound to `IsMember`. It is hidden because no
user will have `user_role = "member"` in the local release. It will reappear
automatically once a shared team is joined via invite code. **No change needed.**

```xml
<!-- Already present and functional — no change needed -->
<Border IsVisible="{Binding IsMember}" ...>
	<Label Text="📖 VIEW-ONLY MODE — Team Admin controls the game" .../>
</Border>
```

---

## 5. `HelpPage.xaml.cs` — Restore View-Only Mode Tip

In `GetHelpHtml()`, re-add the following bullet to the Tips section:

```html
<li>👁️ <strong>View-only mode:</strong> If you joined as a team member (not admin),
	the amber banner shows and controls are disabled.</li>
```

Place it after the "Field time counter" bullet.

---

## 6. Cloud Services — Already Fully Implemented

The following files are complete and require **no changes** to enable cloud features.
They activate automatically once a shared team is created or joined:

| File | Purpose |
|---|---|
| `Services/CloudRosterService.cs` | Saves roster snapshot to Firestore via REST; debounced 2 s |
| `Services/ICloudRosterService.cs` | Interface consumed by `GameViewModel` |
| `Services/FcmService.cs` | Firebase Cloud Messaging token management |
| `Services/FirebaseInitializationService.cs` | Initialises Firebase at app startup via hidden WebView |
| `GamePageSaveBridge.cs` (`FirebaseSaveBridge`) | Static bridge to push game state (roster + scores) to Firestore; receives auth token from `TeamDetailsPage` |
| `CloudSyncHelper.cs` | Static event bus for manual sync requests |
| `ChatPage.xaml` / `ChatPage.xaml.cs` | Full chat UI backed by Firebase Realtime DB; loads team chat by `team_id` from Preferences; sends FCM token on load |

### Firebase project details (already embedded in code)
- **Project ID:** `turf-timer`
- **API Key:** stored as `FirebaseApiKey` constant in `TeamDetailsPage.xaml.cs`, `CloudRosterService.cs`, and `GamePageSaveBridge.cs`

---

## 7. Shared Team Data Flow (Reference)

When a user creates or joins a shared team, the following sequence activates:

```
TeamDetailsPage
  └─ OnCreateTeamClicked / OnJoinTeamClicked
	   ├─ EnsureFirebaseAuthAsync()          → anonymous Firebase sign-in
	   ├─ FirebaseSaveBridge.SetAuthToken()  → passes token to GamePageSaveBridge
	   ├─ Preferences: team_mode = "shared"
	   └─ RefreshAppShellMenu()
			└─ AppShell.UpdateMenuItemAvailability()
				 └─ ChatTab.IsVisible = true

GamePage (on appearing with shared team)
  └─ GameViewModel.IsMember = (user_role == "member")
	   ├─ true  → read-only banner shown, controls blocked
	   └─ false → full admin controls active

CloudRosterService.SaveAsync()
  └─ Preferences (local, always)
  └─ Firestore REST PATCH (if admin and shared team)

ChatPage (when Chat tab opened)
  └─ LoadChatInterface() → WebView with Firebase JS SDK
  └─ FcmService.SaveTokenViaJs → UpdateTokenInFirestoreAsync()
```

---

## 8. `AppShell.xaml` — Re-enable the Location Tab

The **Location** tab (`SetupPage`) allows users to share and view match locations with
other team members. It is only useful in a cloud/shared-team context — a local-only user
has no one to share a location with.

The tab is hidden by setting `IsVisible="False"` on the `ShellContent` element.
To restore it, simply remove that attribute (or set it to `True`):

**Before (local-only release):**
```xml
<ShellContent
    x:Name="LocationTab"
    Title="Location"
    IsVisible="False"
    ContentTemplate="{DataTemplate local:SetupPage}"
    Route="SetupPage" />
```

**After (cloud release):**
```xml
<ShellContent
    x:Name="LocationTab"
    Title="Location"
    ContentTemplate="{DataTemplate local:SetupPage}"
    Route="SetupPage" />
```

> **Note:** You may also want to tie `LocationTab.IsVisible` to the same `!isLocal`
> guard used for `ChatTab` inside `AppShell.UpdateMenuItemAvailability()`, so it
> shows/hides dynamically as the user switches between local and shared teams:
>
> ```csharp
> // AppShell.xaml.cs — inside UpdateMenuItemAvailability()
> ChatTab.IsVisible     = !isLocal;
> LocationTab.IsVisible = !isLocal;  // add this line
> ```

---

## 9. Checklist Summary

| # | File | Action |
|---|---|---|
| 1 | `TeamDetailsPage.xaml` | Show checkbox rows; revert `LocalTeamSection`, `CreateLocalSection`, `CreateTeamNoModeLabel` visibility; restore hint text |
| 2 | `TeamDetailsPage.xaml.cs` | Revert `OnAppearing` to mode-aware logic; revert `UpdateCreateTeamSubSections` |
| 3 | `AppShell.xaml.cs` | No change needed — `ChatTab.IsVisible = !isLocal` already handles Chat; optionally add same guard for `LocationTab` |
| 4 | `AppShell.xaml` | Remove `IsVisible="False"` from `LocationTab` `ShellContent` |
| 5 | `GamePage.xaml` | No change needed — member banner already wired |
| 6 | `HelpPage.xaml.cs` | Re-add view-only mode tip bullet |
| 7 | All cloud service files | No changes needed — fully implemented |
