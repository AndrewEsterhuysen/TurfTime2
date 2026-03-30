// Soccer Team Rotation Manager - JavaScript

class RosterManager {
    constructor() {
        this.rows = []; // { tr, nameInput, cbField, cbBench, cbGoalie, cbInactive, counterSeconds, counterTimerId, counterDisplay, updatePlayerColor }
        this.rosterBody = document.getElementById('rosterBody');
        this.rotateBtn = document.getElementById('rotateBtn');
        this.startBtn = document.getElementById('startBtn');
        this.viewlessBtn = document.getElementById('viewlessBtn');
        this.timerLabel = document.getElementById('matchTimer');
        this.countdownLabel = document.getElementById('countdownTimer');
        this.matchTimeLabelElement = document.querySelector('.top-controls .control-col:first-child .timer-label');

        // Swipeable roster elements
        this.swipeableRoster = document.getElementById('swipeableRoster');
        this.swipeablePlayerList = document.getElementById('swipeablePlayerList');

        // Score tracking
        this.teamAScore = 0;
        this.teamBScore = 0;
        this.teamAScoreBtn = document.getElementById('teamAScoreBtn');
        this.teamBScoreBtn = document.getElementById('teamBScoreBtn');
        this.teamAScoreDisplay = document.getElementById('teamAScore');
        this.teamBScoreDisplay = document.getElementById('teamBScore');

        // Header score displays
        this.headerScoreUs = document.getElementById('scoreUs');
        this.headerScoreThem = document.getElementById('scoreThem');
        this.headerScoreUsValue = document.getElementById('scoreUsValue');
        this.headerScoreThemValue = document.getElementById('scoreThemValue');

        // Modal elements
        this.modalOverlay = document.getElementById('modalOverlay');
        this.modalTitle = document.getElementById('modalTitle');
        this.modalInput = document.getElementById('modalInput');
        this.modalError = document.getElementById('modalError');
        this.modalOk = document.getElementById('modalOk');
        this.modalCancel = document.getElementById('modalCancel');
        this.modalAuto = document.getElementById('modalAuto');

        // Rotation count modal elements
        this.rotationModalOverlay = document.getElementById('rotationModalOverlay');
        this.rotationCountDisplay = document.getElementById('rotationCountDisplay');
        this.rotationIncrement = document.getElementById('rotationIncrement');
        this.rotationDecrement = document.getElementById('rotationDecrement');
        this.rotationModalClose = document.getElementById('rotationModalClose');

        // Table inactive toggle elements
        this.tableInactiveToggle = document.getElementById('tableInactiveToggle');
        this.tableInactiveToggleBtn = document.getElementById('tableInactiveToggleBtn');
        this.tableInactiveCount = document.getElementById('tableInactiveCount');
        this.tableInactiveIcon = document.getElementById('tableInactiveIcon');

        // Rotation count state
        this.rotationCount = 1;

        // Rotation style (1-5)
        this.rotationStyle = 1;
        this.loadRotationStyle();

        // Team view preference (swipe or table)
        this.preferredTeamView = 'swipe'; // Default to swipe
        this.loadTeamViewPreference();

        // Storage
        this.STORAGE_KEY = 'roster.v1';
        this.STORAGE_VERSION = 2; // Increment when structure changes
        this.saveDebounced = this._debounce(() => this.saveToStorage(), 300);

        // Match timer (countdown from 90 minutes, split into halves)
        this.matchDurationSeconds = 90 * 60; // default 90 minutes (total game)
        this.halfDurationSeconds = 0; // will be calculated when Start is pressed
        this.matchRemainingSeconds = this.matchDurationSeconds; // show full duration initially
        this.timerId = null;
        this.timerRunning = false;
        this.initialArrangementDone = false;
        this.currentHalf = 'setup'; // 'setup', '1st', 'halftime', '2nd', 'end'
        this.updateTimerDisplay();
        this.updateMatchTimeLabel();

        // Countdown timer (down from preset)
        this.countdownPreset = 2 * 60; // 2 minutes
        this.countdownRemaining = this.countdownPreset;
        this.countdownId = null;
        this.updateCountdownDisplay();
        this.setRotateAttention(false);

        // Pointers to preserve FIFO order across rotations
        this.lastFieldIdx = -1;
        this.lastBenchIdx = -1;

        this.viewMode = 0; // 0=Swipeable, 1=Less, 2=Rotation
        this.isEditingName = false; // skip sizing updates while editing a name
        this.showInactivePlayers = false; // Toggle for showing inactive players in swipeable and table views

        // Drag and drop state
        this.draggedRow = null;
        this.dragOverRow = null;

        // Initialize game logger
        this.logger = new GameLogger();
        this.logger.setRosterManager(this);

        this.buildRows();
        this.loadFromStorage();
        this.bindEvents();
        this.initializeView(); // Initialize the correct view
        this.markNextPlayers();
        this.updateRotateButtonText(); // Initialize button text with rotation count
        this.updateViewButtonState(); // Initialize view button state
        this.updateStartButtonState(); // Initialize start button state
        this.updateScoreVisibility(); // Initialize score visibility based on game state

        // Save on app background/close
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState !== 'visible') this.saveToStorage();
        });
        window.addEventListener('beforeunload', () => this.saveToStorage());
        window.addEventListener('resize', () => this.updateDynamicSizing());
    }

    bindEvents() {
        // Click on match timer to edit duration (when stopped only)
        this.timerLabel.addEventListener('click', () => {
            if (!this.timerRunning && this.currentHalf === 'setup') {
                this.editMatchDuration();
            }
        });
        this.timerLabel.style.cursor = 'pointer';
        this.timerLabel.style.userSelect = 'none';

        // Click on countdown timer to edit preset (always editable)
        this.countdownLabel.addEventListener('click', () => {
            this.editCountdownPreset();
        });
        this.countdownLabel.style.cursor = 'pointer';
        this.countdownLabel.style.userSelect = 'none';

        // Rotate button - execute N rotations based on rotationCount
        this._rotateBtnLongPressFired = false;
        this._rotateBtnHoldTimer = null;

        this.rotateBtn.addEventListener('click', (e) => {
            if (this._rotateBtnLongPressFired) {
                this._rotateBtnLongPressFired = false;
                e.preventDefault();
                e.stopPropagation();
                return;
            }

            // Execute rotationCount rotations
            for (let i = 0; i < this.rotationCount; i++) {
                this.rotateOnce();
            }
            this.resetCountdown(this.timerRunning);
            this.saveDebounced();

            // Rebuild swipeable roster if in View_A mode
            if (this.viewMode === 0) {
                this.buildSwipeableRoster();
            }

            this.updateDynamicSizing();

            // Update rotation display if in View_D mode
            if (this.viewMode === 2) {
                this.updateRotationDisplay();
            }
        });

        // Long-press detection on rotate button to show rotation count modal
        const rotateHoldStart = () => {
            if (this._rotateBtnHoldTimer) clearTimeout(this._rotateBtnHoldTimer);
            this._rotateBtnLongPressFired = false;
            this._rotateBtnHoldTimer = setTimeout(() => {
                this.showRotationCountModal();
                this._rotateBtnLongPressFired = true;
            }, 500); // 500ms for long press
        };
        const rotateHoldEnd = () => {
            if (this._rotateBtnHoldTimer) {
                clearTimeout(this._rotateBtnHoldTimer);
                this._rotateBtnHoldTimer = null;
            }
        };

        this.rotateBtn.addEventListener('mousedown', rotateHoldStart);
        this.rotateBtn.addEventListener('mouseup', rotateHoldEnd);
        this.rotateBtn.addEventListener('mouseleave', rotateHoldEnd);
        this.rotateBtn.addEventListener('touchstart', (e) => { rotateHoldStart(); }, { passive: true });
        this.rotateBtn.addEventListener('touchend', rotateHoldEnd);
        this.rotateBtn.addEventListener('touchcancel', rotateHoldEnd);
        this.rotateBtn.addEventListener('contextmenu', (e) => { e.preventDefault(); e.stopPropagation(); });
        this.rotateBtn.addEventListener('selectstart', (e) => { e.preventDefault(); e.stopPropagation(); });

        // Rotation count modal events
        this.rotationIncrement.addEventListener('click', () => this.incrementRotationCount());
        this.rotationDecrement.addEventListener('click', () => this.decrementRotationCount());
        this.rotationModalClose.addEventListener('click', () => this.hideRotationCountModal());
        this.rotationModalOverlay.addEventListener('click', (e) => {
            if (e.target === this.rotationModalOverlay) {
                this.hideRotationCountModal();
            }
        });

        // Click toggles start/pause unless long-press already restarted the game
        this._startBtnLongPressFired = false;
        this._startBtnHoldTimer = null;
        this.startBtn.addEventListener('click', (e) => {
            if (this._startBtnLongPressFired) {
                this._startBtnLongPressFired = false;
                e.preventDefault();
                e.stopPropagation();
                return;
            }
            this.toggleStartPause();
        });

        // Long-press detection (3 seconds) to restart game
        const holdStart = () => {
            if (this._startBtnHoldTimer) clearTimeout(this._startBtnHoldTimer);
            this._startBtnLongPressFired = false;
            this._startBtnHoldTimer = setTimeout(() => {
                this.restartGame();
                this._startBtnLongPressFired = true;
            }, 1000);
        };
        const holdEnd = () => {
            if (this._startBtnHoldTimer) {
                clearTimeout(this._startBtnHoldTimer);
                this._startBtnHoldTimer = null;
            }
        };
        this.startBtn.addEventListener('mousedown', holdStart);
        this.startBtn.addEventListener('mouseup', holdEnd);
        this.startBtn.addEventListener('mouseleave', holdEnd);
        this.startBtn.addEventListener('touchstart', (e) => { holdStart(); }, { passive: true });
        this.startBtn.addEventListener('touchend', holdEnd);
        this.startBtn.addEventListener('touchcancel', holdEnd);
        this.startBtn.addEventListener('contextmenu', (e) => { e.preventDefault(); e.stopPropagation(); });
        this.startBtn.addEventListener('selectstart', (e) => { e.preventDefault(); e.stopPropagation(); });
        this.viewlessBtn.addEventListener('click', () => this.toggleLessView());

        // Score button events
        if (this.teamAScoreBtn) {
            this.teamAScoreBtn.addEventListener('click', () => this.incrementTeamAScore());
        }
        if (this.teamBScoreBtn) {
            this.teamBScoreBtn.addEventListener('click', () => this.incrementTeamBScore());
        }

        // Table inactive toggle button event
        if (this.tableInactiveToggleBtn) {
            this.tableInactiveToggleBtn.addEventListener('click', () => this.toggleTableInactive());
        }
    }

    // Initialize view based on current viewMode and team view preference
    initializeView() {
        const panel = document.querySelector('.panel');
        const rotationDisplay = document.getElementById('rotationDisplay');
        const swipeableRoster = this.swipeableRoster;

        // Set viewMode based on team view preference if not in rotation view
        if (this.viewMode !== 2) {
            this.viewMode = this.preferredTeamView === 'swipe' ? 0 : 1;
        }

        if (this.viewMode === 0) {
            // Swipeable view
            this.viewlessBtn.textContent = 'VIEW_A';
            panel.style.display = 'none';
            rotationDisplay.style.display = 'none';
            swipeableRoster.style.display = '';
            this.buildSwipeableRoster();
        } else if (this.viewMode === 1) {
            // Table view
            document.body.classList.add('less-view');
            this.viewlessBtn.textContent = 'VIEW_B';
            panel.style.display = '';
            rotationDisplay.style.display = 'none';
            swipeableRoster.style.display = 'none';
            this.updateTableInactiveRows(); // Update inactive row visibility
        } else if (this.viewMode === 2) {
            // Rotation view
            document.body.classList.add('rotation-view');
            this.viewlessBtn.textContent = 'VIEW_C';
            panel.style.display = 'none';
            rotationDisplay.style.display = '';
            swipeableRoster.style.display = 'none';
            this.updateRotationDisplay();
        }
    }

    // Update Match Time label based on game state
    updateMatchTimeLabel() {
        if (this.currentHalf === 'setup') {
            this.matchTimeLabelElement.textContent = 'Match Time';
        } else if (this.currentHalf === '1st') {
            this.matchTimeLabelElement.textContent = '1st Half';
        } else if (this.currentHalf === 'halftime') {
            this.matchTimeLabelElement.textContent = '1st Half';
        } else if (this.currentHalf === '2nd') {
            this.matchTimeLabelElement.textContent = '2nd Half';
        } else if (this.currentHalf === 'end') {
            this.matchTimeLabelElement.textContent = '2nd Half';
        }
    }

    // Toggle between preferred view and rotation view
    toggleLessView() {
        // Determine the preferred team view mode (0 = swipe, 1 = table)
        const preferredMode = this.preferredTeamView === 'swipe' ? 0 : 1;

        // If switching away from View_A during setup, auto-assign unassigned players to Inactive
        if (this.viewMode === 0 && this.currentHalf === 'setup') {
            this.rows.forEach(r => {
                const hasPos = r.cbField.checked || r.cbBench.checked || r.cbGoalie.checked;
                if (!hasPos) {
                    r.cbInactive.checked = true;
                    r.cbField.checked = false;
                    r.cbBench.checked = false;
                    r.cbGoalie.checked = false;
                    r.updatePlayerColor();
                }
            });
        }

        // Toggle between preferred view and rotation view (VIEW_C)
        if (this.viewMode === 2) {
            // Currently in rotation view, switch to preferred view
            this.viewMode = preferredMode;
        } else {
            // Currently in preferred view, switch to rotation view
            this.viewMode = 2;
        }

        // Update body classes
        document.body.classList.remove('less-view', 'min-view', 'rotation-view');
        const panel = document.querySelector('.panel');
        const rotationDisplay = document.getElementById('rotationDisplay');
        const swipeableRoster = this.swipeableRoster;

        if (this.viewMode === 0) {
            // Swipeable view
            this.viewlessBtn.textContent = 'VIEW_A';
            panel.style.display = 'none';
            rotationDisplay.style.display = 'none';
            swipeableRoster.style.display = '';
            this.buildSwipeableRoster();
        } else if (this.viewMode === 1) {
            // Table view
            document.body.classList.add('less-view');
            this.viewlessBtn.textContent = 'VIEW_B';
            panel.style.display = '';
            rotationDisplay.style.display = 'none';
            swipeableRoster.style.display = 'none';
        } else if (this.viewMode === 2) {
            // Rotation view
            document.body.classList.add('rotation-view');
            this.viewlessBtn.textContent = 'VIEW_C';
            panel.style.display = 'none';
            rotationDisplay.style.display = '';
            swipeableRoster.style.display = 'none';
            this.updateRotationDisplay();
        }

        // Update inactive row visibility
        this.rows.forEach(r => r.tr.classList.toggle('inactive-row', !!r.cbInactive.checked));

        // Update table inactive toggle visibility
        this.updateTableInactiveRows();

        this.updateDynamicSizing();
        setTimeout(() => this.updateDynamicSizing(), 100);
    }

    // Toggle table inactive rows visibility
    toggleTableInactive() {
        this.showInactivePlayers = !this.showInactivePlayers;
        this.updateTableInactiveRows();
        // Trigger dynamic sizing to scale up remaining players
        this.updateDynamicSizing();
        setTimeout(() => this.updateDynamicSizing(), 100);
    }

    // Update table inactive rows visibility and toggle button
    updateTableInactiveRows() {
        // Only apply to table view (viewMode 1)
        if (this.viewMode !== 1) {
            if (this.tableInactiveToggle) {
                this.tableInactiveToggle.style.display = 'none';
            }
            return;
        }

        const inactiveRows = this.rows.filter(r => r.cbInactive.checked);
        const inactiveCount = inactiveRows.length;

        // Hide toggle if no inactive players or in setup phase
        if (inactiveCount === 0 || this.currentHalf === 'setup') {
            if (this.tableInactiveToggle) {
                this.tableInactiveToggle.style.display = 'none';
            }
            // Show all rows in setup by removing inactive-row class
            this.rows.forEach(r => {
                r.tr.classList.remove('inactive-row');
            });
            return;
        }

        // Show toggle button
        if (this.tableInactiveToggle) {
            this.tableInactiveToggle.style.display = '';
        }

        // Update toggle button text and icon
        if (this.tableInactiveCount) {
            this.tableInactiveCount.textContent = `${inactiveCount} Inactive Player${inactiveCount !== 1 ? 's' : ''}`;
        }
        if (this.tableInactiveIcon) {
            this.tableInactiveIcon.textContent = this.showInactivePlayers ? '▲' : '▼';
        }

        // Show/hide inactive rows by managing the inactive-row class
        // The CSS rule .less-view tr.inactive-row { display: none; } will handle visibility
        inactiveRows.forEach(r => {
            if (this.showInactivePlayers) {
                // Remove inactive-row class to show the row
                r.tr.classList.remove('inactive-row');
            } else {
                // Add inactive-row class to hide the row
                r.tr.classList.add('inactive-row');
            }
        });
    }

    // Build swipeable roster for View_A
    buildSwipeableRoster() {
        this.swipeablePlayerList.innerHTML = '';

        // Determine if we should show inactive players
        const hideInactive = this.currentHalf !== 'setup' && !this.showInactivePlayers;

        // Separate active and inactive players
        const activePlayers = [];
        const inactivePlayers = [];

        this.rows.forEach((row, idx) => {
            if (row.cbInactive.checked) {
                inactivePlayers.push({ row, idx });
            } else {
                activePlayers.push({ row, idx });
            }
        });

        // Build active player items
        activePlayers.forEach(({ row, idx }) => {
            const playerItem = this.createSwipeablePlayerItem(row, idx);
            this.swipeablePlayerList.appendChild(playerItem);
        });

        // Add inactive section toggle if there are inactive players and game has started
        if (inactivePlayers.length > 0 && this.currentHalf !== 'setup') {
            const toggleSection = document.createElement('div');
            toggleSection.className = 'inactive-toggle-section';

            const toggleButton = document.createElement('button');
            toggleButton.className = 'inactive-toggle-btn';
            toggleButton.innerHTML = `
                <span class="inactive-count">${inactivePlayers.length} Inactive Player${inactivePlayers.length !== 1 ? 's' : ''}</span>
                <span class="inactive-toggle-icon">${this.showInactivePlayers ? '▲' : '▼'}</span>
            `;

            toggleButton.addEventListener('click', () => {
                this.showInactivePlayers = !this.showInactivePlayers;
                this.buildSwipeableRoster(); // Rebuild to show/hide inactive players
            });

            toggleSection.appendChild(toggleButton);
            this.swipeablePlayerList.appendChild(toggleSection);
        }

        // Build inactive player items (only if showing)
        if (!hideInactive && inactivePlayers.length > 0) {
            inactivePlayers.forEach(({ row, idx }) => {
                const playerItem = this.createSwipeablePlayerItem(row, idx);
                playerItem.classList.add('inactive-player-item'); // Add class for styling
                this.swipeablePlayerList.appendChild(playerItem);
            });
        }

        // Mark next players
        this.markNextPlayersSwipeable();

        // Trigger dynamic sizing after DOM has rendered
        setTimeout(() => this.updateDynamicSizing(), 0);
        setTimeout(() => this.updateDynamicSizing(), 50);
    }

    // Helper method to create a swipeable player item
    createSwipeablePlayerItem(row, idx) {
        const playerItem = document.createElement('div');
        playerItem.className = 'swipeable-player-item';
        playerItem.dataset.playerIndex = idx;

        // Player name text (editable on click)
        const nameSpan = document.createElement('span');
        nameSpan.className = 'player-name-text';
        nameSpan.contentEditable = 'false';
        const icon = this.getPositionIcon(row);
        const playerName = row.nameInput.value || `Player ${idx + 1}`;
        nameSpan.textContent = icon ? `${icon} ${playerName}` : playerName;

        // Make name editable on tap
        nameSpan.addEventListener('click', (e) => {
            if (nameSpan.contentEditable === 'false') {
                e.stopPropagation();
                nameSpan.contentEditable = 'true';
                nameSpan.focus();
                // Select all text
                const range = document.createRange();
                range.selectNodeContents(nameSpan);
                const sel = window.getSelection();
                sel.removeAllRanges();
                sel.addRange(range);
            }
        });

        nameSpan.addEventListener('blur', () => {
            nameSpan.contentEditable = 'false';
            let newName = nameSpan.textContent.trim();
            // Remove ALL icons if user included them
            newName = newName.replace(/^([⚽💺🥅❌]\s*)+/, '');
            if (newName) {
                row.nameInput.value = newName;
                // Re-add icon
                const icon = this.getPositionIcon(row);
                nameSpan.textContent = icon ? `${icon} ${newName}` : newName;
                this.saveDebounced();
            } else {
                const icon = this.getPositionIcon(row);
                const playerName = row.nameInput.value || `Player ${idx + 1}`;
                nameSpan.textContent = icon ? `${icon} ${playerName}` : playerName;
            }
        });

        nameSpan.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                nameSpan.blur();
            }
        });

        playerItem.appendChild(nameSpan);

        // Counter display
        const counterSpan = document.createElement('span');
        counterSpan.className = 'player-counter';
        counterSpan.textContent = row.counterDisplay.textContent;
        playerItem.appendChild(counterSpan);

        // Swipe hint arrows
        const leftHint = document.createElement('span');
        leftHint.className = 'swipe-hint swipe-hint-left';
        leftHint.textContent = '⬅';
        playerItem.appendChild(leftHint);

        const rightHint = document.createElement('span');
        rightHint.className = 'swipe-hint swipe-hint-right';
        rightHint.textContent = '➡';
        playerItem.appendChild(rightHint);

        // Apply current status color
        this.updateSwipeablePlayerColor(playerItem, row);

        // Add to player item for reference
        row.swipeableElement = playerItem;

        // Add swipe gesture handlers
        this.enableSwipeGesture(playerItem, row);

        // Add drag and drop for reordering
        this.enableSwipeableDragAndDrop(playerItem, row);

        return playerItem;
    }

    // Get position icon for a player
    getPositionIcon(row) {
        if (row.cbField.checked) return '⚽';
        if (row.cbBench.checked) return '💺';
        if (row.cbGoalie.checked) return '🥅';
        if (row.cbInactive.checked) return '❌';
        return ''; // No icon for unassigned
    }

    // Update swipeable player color based on current state
    updateSwipeablePlayerColor(playerItem, row) {
        playerItem.classList.remove('player-field', 'player-bench', 'player-goalie', 'player-inactive', 'player-none');

        if (row.cbField.checked) {
            playerItem.classList.add('player-field');
        } else if (row.cbBench.checked) {
            playerItem.classList.add('player-bench');
        } else if (row.cbGoalie.checked) {
            playerItem.classList.add('player-goalie');
        } else if (row.cbInactive.checked) {
            playerItem.classList.add('player-inactive');
        } else {
            playerItem.classList.add('player-none');
        }

        // Update position icon in name text
        const nameText = playerItem.querySelector('.player-name-text');
        if (nameText) {
            const icon = this.getPositionIcon(row);
            const currentText = nameText.textContent;
            // Remove ALL icons at the start (handles multiple icons if accumulated)
            const nameWithoutIcon = currentText.replace(/^([⚽💺🥅❌]\s*)+/, '');
            nameText.textContent = icon ? `${icon} ${nameWithoutIcon}` : nameWithoutIcon;
        }
    }

    // Enable swipe gesture on player item
    enableSwipeGesture(playerItem, row) {
        let startX = 0;
        let startY = 0;
        let currentX = 0;
        let currentY = 0;
        let startTime = 0;
        let longPressTimer = null;
        let isLongPress = false;
        let isDraggingReorder = false;
        let dragOverIndex = -1;
        const swipeThreshold = 50; // Reduced threshold for quicker response
        const longPressDelay = 500; // 500ms for long press

        const handleStart = (e) => {
            startTime = Date.now();
            startX = e.type.includes('mouse') ? e.clientX : e.touches[0].clientX;
            startY = e.type.includes('mouse') ? e.clientY : e.touches[0].clientY;
            currentX = startX;
            currentY = startY;
            isLongPress = false;
            isDraggingReorder = false;
            dragOverIndex = -1;

            // Start long-press timer
            longPressTimer = setTimeout(() => {
                isLongPress = true;
                isDraggingReorder = true;
                playerItem.classList.add('dragging');
                // Disable swipe hints during reorder
                playerItem.style.cursor = 'grabbing';
            }, longPressDelay);
        };

        const handleMove = (e) => {
            currentX = e.type.includes('mouse') ? e.clientX : e.touches[0].clientX;
            currentY = e.type.includes('mouse') ? e.clientY : e.touches[0].clientY;
            const deltaX = currentX - startX;
            const deltaY = currentY - startY;

            // If long press activated, handle vertical reordering
            if (isDraggingReorder) {
                e.preventDefault();

                // Visual feedback for vertical drag
                playerItem.style.transform = `translateY(${deltaY}px)`;

                // Find which player item we're over
                const items = Array.from(this.swipeablePlayerList.children);
                const myIndex = items.indexOf(playerItem);

                items.forEach((item, idx) => {
                    if (item === playerItem) return;
                    const rect = item.getBoundingClientRect();
                    const midY = rect.top + rect.height / 2;

                    if (Math.abs(currentY - midY) < rect.height / 2) {
                        item.classList.add('drag-over');
                        dragOverIndex = idx;
                    } else {
                        item.classList.remove('drag-over');
                    }
                });
                return;
            }

            // If not long press yet, check if we should cancel it (horizontal swipe detected)
            if (Math.abs(deltaX) > 10 || Math.abs(deltaY) > 10) {
                if (longPressTimer) {
                    clearTimeout(longPressTimer);
                    longPressTimer = null;
                }
            }

            // Show horizontal swipe feedback
            if (Math.abs(deltaX) > 5 && !isLongPress) {
                playerItem.classList.add('swiping');
                playerItem.style.transform = `translateX(${deltaX}px)`;
                e.preventDefault();
            }
        };

        const handleEnd = (e) => {
            // Clear long press timer
            if (longPressTimer) {
                clearTimeout(longPressTimer);
                longPressTimer = null;
            }

            const deltaX = currentX - startX;
            const deltaY = currentY - startY;
            const duration = Date.now() - startTime;
            const totalMovement = Math.sqrt(deltaX * deltaX + deltaY * deltaY);

            // Handle reorder drop
            if (isDraggingReorder) {
                playerItem.classList.remove('dragging');
                playerItem.style.transform = '';
                playerItem.style.cursor = '';

                // Remove all drag-over classes
                Array.from(this.swipeablePlayerList.children).forEach(item => {
                    item.classList.remove('drag-over');
                });

                // Execute reorder if valid drop target
                if (dragOverIndex !== -1) {
                    const myIndex = this.rows.indexOf(row);
                    if (myIndex !== -1 && myIndex !== dragOverIndex) {
                        // Find which rows are currently marked as "next" before reordering
                        const benchCandidates = [];
                        const fieldCandidates = [];
                        this.rows.forEach((r, idx) => {
                            if (r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked) benchCandidates.push(idx);
                            if (r.cbField.checked && !r.cbInactive.checked && !r.cbGoalie.checked) fieldCandidates.push(idx);
                        });

                        // Get the current "next" rows before reordering
                        const nextFieldIdx = this._nextIndexFrom(fieldCandidates, this.lastFieldIdx);
                        const nextBenchIdx = this._nextIndexFrom(benchCandidates, this.lastBenchIdx);
                        const nextFieldRow = nextFieldIdx !== -1 ? this.rows[nextFieldIdx] : null;
                        const nextBenchRow = nextBenchIdx !== -1 ? this.rows[nextBenchIdx] : null;

                        // Reorder array
                        this.rows.splice(myIndex, 1);
                        this.rows.splice(dragOverIndex, 0, row);

                        // Rebuild DOM
                        this.rebuildDOM();
                        setTimeout(() => {
                            this.buildSwipeableRoster();
                        }, 50);

                        // Update pointers to point to the new positions of the same "next" rows
                        if (nextFieldRow) {
                            const newFieldIdx = this.rows.indexOf(nextFieldRow);
                            if (newFieldIdx !== -1) {
                                // Set lastFieldIdx so that nextFieldRow is still the next one
                                this.lastFieldIdx = (newFieldIdx - 1 + this.rows.length) % this.rows.length;
                            }
                        }
                        if (nextBenchRow) {
                            const newBenchIdx = this.rows.indexOf(nextBenchRow);
                            if (newBenchIdx !== -1) {
                                // Set lastBenchIdx so that nextBenchRow is still the next one
                                this.lastBenchIdx = (newBenchIdx - 1 + this.rows.length) % this.rows.length;
                            }
                        }

                        // Log reordering
                        if (this.logger) {
                            this.logger.log(
                                this.logger.EVENT_TYPES.PLAYER_REORDERED,
                                `${row.nameInput.value} moved from position ${myIndex + 1} to ${dragOverIndex + 1}`,
                                row.nameInput.value,
                                { fromIndex: myIndex, toIndex: dragOverIndex }
                            );
                        }

                        this.saveDebounced();
                    }
                }

                isDraggingReorder = false;
                isLongPress = false;
                return;
            }

            // Detect short tap (< 200ms, minimal movement)
            if (duration < 200 && totalMovement < 10) {
                // Short tap detected
                if (this.currentHalf === 'setup') {
                    // During setup: allow name editing (handled by name span click event)
                    // Do nothing here
                } else {
                    // After game starts: set as next player to rotate
                    this.setNextPlayerToRotate(row);
                }
                isLongPress = false;
                return;
            }

            // Handle horizontal swipe
            playerItem.classList.remove('swiping');
            playerItem.style.transform = '';

            // Quick swipe detection - lower threshold for quick swipes
            const isQuickSwipe = duration < 300;
            const effectiveThreshold = isQuickSwipe ? 30 : swipeThreshold;

            if (Math.abs(deltaX) > effectiveThreshold) {
                if (deltaX < 0) {
                    this.handleSwipeLeft(row);
                } else {
                    this.handleSwipeRight(row);
                }

                // Update the visual appearance immediately on the current playerItem
                this.updateSwipeablePlayerColor(playerItem, row);
                this.markNextPlayersSwipeable();
                this.updateViewButtonState();
                this.updateStartButtonState();
            }

            isLongPress = false;
        };

        const handleCancel = () => {
            if (longPressTimer) {
                clearTimeout(longPressTimer);
                longPressTimer = null;
            }

            playerItem.classList.remove('swiping', 'dragging');
            playerItem.style.transform = '';
            playerItem.style.cursor = '';

            Array.from(this.swipeablePlayerList.children).forEach(item => {
                item.classList.remove('drag-over');
            });

            isDraggingReorder = false;
            isLongPress = false;
        };

        // Mouse events
        playerItem.addEventListener('mousedown', handleStart);
        playerItem.addEventListener('mousemove', handleMove);
        playerItem.addEventListener('mouseup', handleEnd);
        playerItem.addEventListener('mouseleave', handleCancel);

        // Touch events
        playerItem.addEventListener('touchstart', handleStart, { passive: false });
        playerItem.addEventListener('touchmove', handleMove, { passive: false });
        playerItem.addEventListener('touchend', handleEnd);
        playerItem.addEventListener('touchcancel', handleCancel);
    }

    // Enable drag and drop for reordering in swipeable view - now handled in enableSwipeGesture
    enableSwipeableDragAndDrop(playerItem, row) {
        // This method is now integrated into enableSwipeGesture for better coordination
        // Keeping it as a stub to avoid breaking the buildSwipeableRoster call
    }

    // Handle swipe left logic
    handleSwipeLeft(row) {
        const previousPosition = this.getCurrentPosition(row);

        if (row.cbInactive.checked || (!row.cbField.checked && !row.cbBench.checked && !row.cbGoalie.checked)) {
            // Inactive → Field
            row.cbInactive.checked = false;
            row.cbBench.checked = false;
            row.cbGoalie.checked = false;
            row.cbField.checked = true;
            this.logPositionChange(row, previousPosition, 'field');
            if (this.timerRunning) this.startCounter(row);
        } else if (row.cbBench.checked) {
            // Bench → Field
            row.cbBench.checked = false;
            row.cbInactive.checked = false;
            row.cbGoalie.checked = false;
            row.cbField.checked = true;
            this.logPositionChange(row, previousPosition, 'field');
            if (this.timerRunning) this.startCounter(row);
        } else if (row.cbField.checked) {
            // Field → Goalie
            // Uncheck any other goalie first
            this.rows.forEach(r => {
                if (r !== row && r.cbGoalie.checked) {
                    r.cbGoalie.checked = false;
                    this.stopCounter(r);
                    r.updatePlayerColor();
                    // Update swipeable element if exists
                    if (r.swipeableElement) {
                        this.updateSwipeablePlayerColor(r.swipeableElement, r);
                    }
                }
            });
            row.cbField.checked = false;
            row.cbBench.checked = false;
            row.cbInactive.checked = false;
            row.cbGoalie.checked = true;
            this.logPositionChange(row, previousPosition, 'goalie');
        } else if (row.cbGoalie.checked) {
            // Goalie → Field
            row.cbGoalie.checked = false;
            row.cbBench.checked = false;
            row.cbInactive.checked = false;
            row.cbField.checked = true;
            this.logPositionChange(row, previousPosition, 'field');
        }

        row.updatePlayerColor();
        this.saveDebounced();
    }

    // Handle swipe right logic
    handleSwipeRight(row) {
        const previousPosition = this.getCurrentPosition(row);

        if (row.cbInactive.checked || (!row.cbField.checked && !row.cbBench.checked && !row.cbGoalie.checked)) {
            // Inactive → Bench
            row.cbInactive.checked = false;
            row.cbField.checked = false;
            row.cbGoalie.checked = false;
            row.cbBench.checked = true;
            this.logPositionChange(row, previousPosition, 'bench');
            this.stopCounter(row);
        } else if (row.cbField.checked) {
            // Field → Bench
            row.cbField.checked = false;
            row.cbInactive.checked = false;
            row.cbGoalie.checked = false;
            row.cbBench.checked = true;
            this.logPositionChange(row, previousPosition, 'bench');
            this.stopCounter(row);
        } else if (row.cbGoalie.checked) {
            // Goalie → Bench
            row.cbGoalie.checked = false;
            row.cbField.checked = false;
            row.cbInactive.checked = false;
            row.cbBench.checked = true;
            this.logPositionChange(row, previousPosition, 'bench');
            this.stopCounter(row);
        } else if (row.cbBench.checked) {
            // Bench → Inactive
            row.cbBench.checked = false;
            row.cbField.checked = false;
            row.cbGoalie.checked = false;
            row.cbInactive.checked = true;
            this.logPositionChange(row, previousPosition, 'inactive');
            this.stopCounter(row);
        }

        row.updatePlayerColor();
        this.saveDebounced();
    }

    // Get current position of a player
    getCurrentPosition(row) {
        if (row.cbField.checked) return 'field';
        if (row.cbBench.checked) return 'bench';
        if (row.cbGoalie.checked) return 'goalie';
        if (row.cbInactive.checked) return 'inactive';
        return 'none';
    }

    // Log position change
    logPositionChange(row, fromPosition, toPosition) {
        if (!this.logger) return;

        const playerName = row.nameInput.value;
        const playerIndex = this.rows.indexOf(row);

        const eventTypeMap = {
            field: this.logger.EVENT_TYPES.PLAYER_TO_FIELD,
            bench: this.logger.EVENT_TYPES.PLAYER_TO_BENCH,
            goalie: this.logger.EVENT_TYPES.PLAYER_TO_GOALIE,
            inactive: this.logger.EVENT_TYPES.PLAYER_TO_INACTIVE
        };

        const eventType = eventTypeMap[toPosition] || this.logger.EVENT_TYPES.PLAYER_TO_FIELD;

        this.logger.log(
            eventType,
            `${playerName} moved to ${toPosition}`,
            playerName,
            {
                fromPosition: fromPosition,
                toPosition: toPosition,
                playerIndex: playerIndex
            }
        );
    }

    // Set next player to rotate (called when player item is tapped after game starts)
    setNextPlayerToRotate(row) {
        const playerIndex = this.rows.indexOf(row);
        if (playerIndex === -1) return;

        // Check if player is on field (and eligible for rotation)
        if (row.cbField.checked && !row.cbInactive.checked && !row.cbGoalie.checked) {
            // Set this as the next field player to rotate
            this.lastFieldIdx = playerIndex - 1; // Set to previous so this becomes next
            this.markNextPlayers();
            this.markNextPlayersSwipeable();

            // Log the action
            if (this.logger) {
                this.logger.log(
                    this.logger.EVENT_TYPES.PLAYER_TO_FIELD, // Using existing event type
                    `${row.nameInput.value} set as next field player to rotate`,
                    row.nameInput.value,
                    { playerIndex: playerIndex, action: 'set_next_to_rotate' }
                );
            }
        }
        // Check if player is on bench (and eligible for rotation)
        else if (row.cbBench.checked && !row.cbInactive.checked && !row.cbGoalie.checked) {
            // Set this as the next bench player to rotate
            this.lastBenchIdx = playerIndex - 1; // Set to previous so this becomes next
            this.markNextPlayers();
            this.markNextPlayersSwipeable();

            // Log the action
            if (this.logger) {
                this.logger.log(
                    this.logger.EVENT_TYPES.PLAYER_TO_BENCH, // Using existing event type
                    `${row.nameInput.value} set as next bench player to rotate`,
                    row.nameInput.value,
                    { playerIndex: playerIndex, action: 'set_next_to_rotate' }
                );
            }
        }
        // If player is goalie or inactive, do nothing
    }

    // Mark next players in swipeable view
    markNextPlayersSwipeable() {
        if (this.viewMode !== 0) return;

        const benchCandidates = [];
        const fieldCandidates = [];
        this.rows.forEach((r, idx) => {
            if (r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked) benchCandidates.push(idx);
            if (r.cbField.checked && !r.cbInactive.checked && !r.cbGoalie.checked) fieldCandidates.push(idx);
        });

        // Remove all next markers and style classes
        this.rows.forEach(r => {
            if (r.swipeableElement) {
                r.swipeableElement.classList.remove('player-next');
                for (let i = 1; i <= 5; i++) {
                    r.swipeableElement.classList.remove(`rotate-style-${i}`);
                }
            }
        });

        // Mark rotationCount number of players for rotation
        const rotations = Math.min(this.rotationCount, benchCandidates.length, fieldCandidates.length);

        for (let i = 0; i < rotations; i++) {
            const nextFieldIdx = this._nextIndexFromWithOffset(fieldCandidates, this.lastFieldIdx, i);
            const nextBenchIdx = this._nextIndexFromWithOffset(benchCandidates, this.lastBenchIdx, i);

            if (nextFieldIdx !== -1 && this.rows[nextFieldIdx].swipeableElement) {
                this.rows[nextFieldIdx].swipeableElement.classList.add('player-next');
                this.rows[nextFieldIdx].swipeableElement.classList.add(`rotate-style-${this.rotationStyle}`);
            }
            if (nextBenchIdx !== -1 && this.rows[nextBenchIdx].swipeableElement) {
                this.rows[nextBenchIdx].swipeableElement.classList.add('player-next');
                this.rows[nextBenchIdx].swipeableElement.classList.add(`rotate-style-${this.rotationStyle}`);
            }
        }
    }

    // Compute dynamic sizes for Less view based on viewport and visible rows
    updateDynamicSizing() {
        if (this.isEditingName) return;
        if (this.viewMode === 0) { // Swipeable view - dynamic sizing for all 16 players
            const swipeableRoster = this.swipeableRoster;

            if (!swipeableRoster || swipeableRoster.style.display === 'none') {
                // Can't measure if not visible, clear properties and return
                const b = document.body.style;
                b.removeProperty('--swipeable-item-height');
                b.removeProperty('--swipeable-font-size');
                b.removeProperty('--swipeable-pad-v');
                b.removeProperty('--swipeable-pad-h');
                b.removeProperty('--swipeable-counter-fs');
                return;
            }

            const bottomControls = document.querySelector('.bottom-controls');
            const rosterRect = swipeableRoster.getBoundingClientRect();
            const bottomH = bottomControls ? bottomControls.getBoundingClientRect().height : 0;
            const bufferSpace = 30; // Large buffer for safety

            // Calculate available height within the roster container
            const available = Math.max(200, window.innerHeight - rosterRect.top - bottomH - bufferSpace);

            // Account for swipeable roster padding and border
            const rosterPadding = 16; // 8px top + 8px bottom
            const rosterBorder = 2;

            // Account for inactive toggle button if present
            const inactiveCount = this.rows.filter(r => r.cbInactive.checked).length;
            const toggleHeight = (inactiveCount > 0 && this.currentHalf !== 'setup') ? 58 : 0; // Toggle button + padding

            const availableForPlayers = Math.max(150, available - rosterPadding - rosterBorder - toggleHeight);

            // Count VISIBLE players (exclude inactive if hidden)
            let visiblePlayerCount = this.rows.length;
            if (this.currentHalf !== 'setup' && !this.showInactivePlayers) {
                // Count only non-inactive players
                visiblePlayerCount = this.rows.filter(r => !r.cbInactive.checked).length;
            }

            // Ensure at least 1 player to avoid division by zero
            const playerCount = Math.max(1, visiblePlayerCount);
            const gapCount = playerCount - 1;
            const gapSize = 3; // Match CSS gap
            const totalGapSpace = gapCount * gapSize;

            // Calculate item height based on VISIBLE player count
            const playerItemHeight = Math.max(22, Math.floor((availableForPlayers - totalGapSpace) / playerCount));

            // Scale font and padding based on item height
            const playerFontSize = Math.max(0.6, Math.min(playerItemHeight * 0.042, 1.2));
            const playerPadV = Math.max(0, Math.min(playerItemHeight * 0.15, 12));
            const playerPadH = Math.max(6, Math.min(playerItemHeight * 0.30, 20));
            const counterFontSize = Math.max(0.5, Math.min(playerItemHeight * 0.035, 0.9));

            const bodyStyle = document.body.style;
            bodyStyle.setProperty('--swipeable-item-height', playerItemHeight + 'px');
            bodyStyle.setProperty('--swipeable-font-size', playerFontSize + 'rem');
            bodyStyle.setProperty('--swipeable-pad-v', playerPadV + 'px');
            bodyStyle.setProperty('--swipeable-pad-h', playerPadH + 'px');
            bodyStyle.setProperty('--swipeable-counter-fs', counterFontSize + 'rem');

            // Clear other view properties
            bodyStyle.removeProperty('--row-h');
            bodyStyle.removeProperty('--cell-pad-v');
            bodyStyle.removeProperty('--cell-pad-h');
            bodyStyle.removeProperty('--input-h');
            bodyStyle.removeProperty('--input-fs');
            bodyStyle.removeProperty('--cb-scale');
            bodyStyle.removeProperty('--rotation-name-fs');
            bodyStyle.removeProperty('--rotation-name-pad');
            bodyStyle.removeProperty('--rotation-title-fs');
            bodyStyle.removeProperty('--rotation-title-mb');
            return;
        }

        const container = document.querySelector('.container');
        const bottomControls = document.querySelector('.bottom-controls');
        const bottomH = bottomControls ? bottomControls.getBoundingClientRect().height : 0;
        const containerStyle = getComputedStyle(container);
        const paddingBottom = parseFloat(containerStyle.paddingBottom || '0');
        const bufferSpace = 20;

        if (this.viewMode === 2) { // Rotation view (View_D)
            const rotationDisplay = document.getElementById('rotationDisplay');
            if (!rotationDisplay) return;

            const rotationTitle = rotationDisplay.querySelector('.rotation-title');
            const rotationPairs = document.getElementById('rotationPairs');

            const displayTop = rotationDisplay.getBoundingClientRect().top;
            const available = Math.max(0, window.innerHeight - displayTop - bottomH - paddingBottom - bufferSpace);

            // Count rotation name elements (bench + field pairs)
            const nameCount = rotationPairs ? rotationPairs.querySelectorAll('.rotation-name').length : 0;

            if (nameCount === 0) return; // No pairs to display

            // Reserve space for title and display padding (more conservative)
            const displayPadding = 50; // top and bottom padding from .rotation-display plus borders
            const titleHeight = 60; // Title space including margin
            const gapCount = nameCount - 1; // gaps between elements

            // Calculate space available for names after accounting for fixed elements
            const availableForNames = Math.max(0, available - titleHeight - displayPadding);

            // Estimate total gap space (will be set dynamically)
            const gapSize = Math.max(2, Math.min(8, availableForNames / (nameCount * 10))); // More conservative gap
            const totalGapSpace = gapCount * gapSize;

            // Calculate per-name height
            const nameHeight = Math.max(30, (availableForNames - totalGapSpace) / nameCount);

            // Dynamic sizing for rotation view (more conservative multipliers)
            const rotationNameFs = Math.max(12, Math.min(nameHeight * 0.45, 24));
            const rotationNamePad = Math.max(4, Math.min(nameHeight * 0.15, 12));
            const rotationTitleFs = Math.max(16, Math.min(nameHeight * 0.5, 28));
            const rotationTitleMb = Math.max(6, Math.min(nameHeight * 0.2, 16));

            // Also size buttons and timers proportionally
            const btnFont = Math.max(12, Math.min(nameHeight * 0.3, 18)) + 'px';
            const btnPadV = Math.max(4, Math.min(nameHeight * 0.15, 10));
            const btnPadH = Math.max(8, Math.min(nameHeight * 0.22, 16));
            const timerFs = Math.max(12, Math.min(nameHeight * 0.32, 18));
            const timerPadV = Math.max(3, Math.min(nameHeight * 0.08, 8));
            const timerPadH = Math.max(6, Math.min(nameHeight * 0.18, 12));

            const bodyStyle = document.body.style;
            bodyStyle.setProperty('--rotation-name-fs', rotationNameFs + 'px');
            bodyStyle.setProperty('--rotation-name-pad', rotationNamePad + 'px');
            bodyStyle.setProperty('--rotation-title-fs', rotationTitleFs + 'px');
            bodyStyle.setProperty('--rotation-title-mb', rotationTitleMb + 'px');
            bodyStyle.setProperty('--rotation-gap', gapSize + 'px');
            bodyStyle.setProperty('--btn-font', btnFont);
            bodyStyle.setProperty('--btn-pad-v', btnPadV + 'px');
            bodyStyle.setProperty('--btn-pad-h', btnPadH + 'px');
            bodyStyle.setProperty('--timer-fs', timerFs + 'px');
            bodyStyle.setProperty('--timer-pad-v', timerPadV + 'px');
            bodyStyle.setProperty('--timer-pad-h', timerPadH + 'px');
            return;
        }

        // Less view (1) uses dynamic sizing for table rows
        // Count VISIBLE rows (exclude inactive if hidden)
        let visibleRowCount = this.rows.length;
        if (this.currentHalf !== 'setup' && !this.showInactivePlayers) {
            // Count only non-inactive players
            visibleRowCount = this.rows.filter(r => !r.cbInactive.checked).length;
        }

        const count = Math.max(1, visibleRowCount); // At least 1 to avoid division by zero
        const tbody = this.rosterBody;
        const table = tbody.closest('table');

        const tbodyTop = tbody.getBoundingClientRect().top;
        const thead = table.querySelector('thead');
        const theadH = thead ? thead.getBoundingClientRect().height : 30;

        // Account for inactive toggle button height if present
        const inactiveCount = this.rows.filter(r => r.cbInactive.checked).length;
        const toggleHeight = (inactiveCount > 0 && this.currentHalf !== 'setup') ? 58 : 0;

        const available = Math.max(0, window.innerHeight - tbodyTop - bottomH - paddingBottom - bufferSpace - toggleHeight);
        const rowH = (available - theadH) / count;

        const cellPadV = rowH * 0.2;
        const cellPadH = rowH * 0.15;
        const inputH = rowH * 0.6;
        const inputFs = rowH * 0.35;
        const cbScale = rowH / 30;
        const btnFont = Math.max(14, rowH * 0.35) + 'px';
        const btnPadV = rowH * 0.2;
        const btnPadH = rowH * 0.35;
        const timerFs = rowH * 0.35;
        const timerPadV = rowH * 0.12;
        const timerPadH = rowH * 0.3;

        const bodyStyle = document.body.style;
        bodyStyle.setProperty('--row-h', rowH + 'px');
        bodyStyle.setProperty('--cell-pad-v', cellPadV + 'px');
        bodyStyle.setProperty('--cell-pad-h', cellPadH + 'px');
        bodyStyle.setProperty('--input-h', inputH + 'px');
        bodyStyle.setProperty('--input-fs', inputFs + 'px');
        bodyStyle.setProperty('--cb-scale', cbScale.toString());
        bodyStyle.setProperty('--btn-font', btnFont);
        bodyStyle.setProperty('--btn-pad-v', btnPadV + 'px');
        bodyStyle.setProperty('--btn-pad-h', btnPadH + 'px');
        bodyStyle.setProperty('--timer-fs', timerFs + 'px');
        bodyStyle.setProperty('--timer-pad-v', timerPadV + 'px');
        bodyStyle.setProperty('--timer-pad-h', timerPadH + 'px');
    }

    // Update field player visibility for Min View
    updateMinViewVisibility() {
        if (this.viewMode !== 2) return;
        
        // Count bench players
        const benchCount = this.rows.filter(r => r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked).length;
        
        // Get field players (excluding goalie)
        const fieldPlayers = [];
        this.rows.forEach((r, idx) => {
            if (r.cbField.checked && !r.cbInactive.checked && !r.cbGoalie.checked) {
                fieldPlayers.push({ row: r, index: idx });
            }
        });
        
        if (fieldPlayers.length === 0 || benchCount === 0) {
            // No hiding needed
            this.rows.forEach(r => r.tr.classList.remove('min-hidden'));
            return;
        }
        
        // Find next field player to rotate (starting point)
        const nextFieldIdx = this._nextIndexFrom(
            fieldPlayers.map(fp => fp.index),
            this.lastFieldIdx
        );
        
        // Hide all field players first
        fieldPlayers.forEach(fp => fp.row.tr.classList.add('min-hidden'));
        
        // Show benchCount field players starting from nextFieldIdx
        let shown = 0;
        for (let i = 0; i < fieldPlayers.length && shown < benchCount; i++) {
            const currentIdx = (fieldPlayers.findIndex(fp => fp.index === nextFieldIdx) + i) % fieldPlayers.length;
            if (currentIdx >= 0 && currentIdx < fieldPlayers.length) {
                fieldPlayers[currentIdx].row.tr.classList.remove('min-hidden');
                shown++;
            }
        }
    }

    // Update rotation display for View_D
    updateRotationDisplay() {
        const rotationPairs = document.getElementById('rotationPairs');
        rotationPairs.innerHTML = '';

        // Get bench and field player indices (just the index numbers)
        const benchIndices = this.rows
            .map((r, i) => ({ row: r, index: i }))
            .filter(item => item.row.cbBench.checked)
            .map(item => item.index);

        const fieldIndices = this.rows
            .map((r, i) => ({ row: r, index: i }))
            .filter(item => item.row.cbField.checked)
            .map(item => item.index);

        if (benchIndices.length === 0 || fieldIndices.length === 0) {
            rotationPairs.innerHTML = '<div class="rotation-empty">No rotations available</div>';
            return;
        }

        // Find the next bench and field indices
        const nextBenchIdx = this._nextIndexFrom(benchIndices, this.lastBenchIdx);
        const nextFieldIdx = this._nextIndexFrom(fieldIndices, this.lastFieldIdx);

        if (nextBenchIdx === -1 || nextFieldIdx === -1) {
            rotationPairs.innerHTML = '<div class="rotation-empty">No rotations available</div>';
            return;
        }

        // Create rotation pairs based on rotation count
        const count = Math.min(this.rotationCount, benchIndices.length, fieldIndices.length);

        for (let i = 0; i < count; i++) {
            // Get bench and field player indices with offset
            const benchIdx = this._nextIndexFromWithOffset(benchIndices, this.lastBenchIdx, i);
            const fieldIdx = this._nextIndexFromWithOffset(fieldIndices, this.lastFieldIdx, i);

            if (benchIdx !== -1 && fieldIdx !== -1) {
                const benchRow = this.rows[benchIdx];
                const fieldRow = this.rows[fieldIdx];

                if (benchRow && fieldRow) {
                    // Strip icons from names before displaying (they already have icons in nameInput.value)
                    const benchName = (benchRow.nameInput.value || `Player ${benchIdx + 1}`).replace(/^([⚽💺🥅❌]\s*)+/, '');
                    const fieldName = (fieldRow.nameInput.value || `Player ${fieldIdx + 1}`).replace(/^([⚽💺🥅❌]\s*)+/, '');

                    // Bench player (left aligned)
                    const benchDiv = document.createElement('div');
                    benchDiv.className = 'rotation-name rotation-bench';
                    benchDiv.textContent = `💺 ${benchName} ➜`;
                    rotationPairs.appendChild(benchDiv);

                    // Field player (right aligned)
                    const fieldDiv = document.createElement('div');
                    fieldDiv.className = 'rotation-name rotation-field';
                    fieldDiv.textContent = `➜ ⚽ ${fieldName}`;
                    rotationPairs.appendChild(fieldDiv);
                }
            }
        }

        // Trigger dynamic sizing after DOM update
        setTimeout(() => this.updateDynamicSizing(), 10);
    }

    // Custom modal dialog helper
    showModal(title, defaultValue, validator, autoHandler = null) {
        return new Promise((resolve) => {
            this.modalTitle.textContent = title;
            this.modalInput.value = defaultValue;
            this.modalError.textContent = '';
            this.modalOverlay.classList.add('active');

            // Show/hide Auto button based on whether autoHandler is provided
            if (autoHandler) {
                this.modalAuto.style.display = '';
            } else {
                this.modalAuto.style.display = 'none';
            }

            this.modalInput.focus();
            this.modalInput.select();

            const cleanup = () => {
                this.modalOverlay.classList.remove('active');
                this.modalOk.removeEventListener('click', okHandler);
                this.modalCancel.removeEventListener('click', cancelHandler);
                this.modalInput.removeEventListener('keydown', keyHandler);
                if (autoHandler) {
                    this.modalAuto.removeEventListener('click', autoClickHandler);
                }
            };

            const okHandler = () => {
                const value = this.modalInput.value.trim();
                const error = validator(value);
                if (error) {
                    this.modalError.textContent = error;
                    this.modalInput.focus();
                    this.modalInput.select();
                } else {
                    cleanup();
                    resolve(value);
                }
            };

            const cancelHandler = () => {
                cleanup();
                resolve(null);
            };

            const keyHandler = (e) => {
                if (e.key === 'Enter') {
                    e.preventDefault();
                    okHandler();
                } else if (e.key === 'Escape') {
                    e.preventDefault();
                    cancelHandler();
                }
            };

            const autoClickHandler = () => {
                const autoValue = autoHandler();
                if (autoValue !== null) {
                    this.modalInput.value = autoValue;
                    this.modalError.textContent = '';
                    this.modalInput.focus();
                    this.modalInput.select();
                }
            };

            this.modalOk.addEventListener('click', okHandler);
            this.modalCancel.addEventListener('click', cancelHandler);
            this.modalInput.addEventListener('keydown', keyHandler);
            if (autoHandler) {
                this.modalAuto.addEventListener('click', autoClickHandler);
            }
        });
    }

    // Edit match duration via custom modal
    async editMatchDuration() {
        const currentMinutes = Math.floor(this.matchDurationSeconds / 60);
        
        const input = await this.showModal(
            'Enter match duration in minutes:',
            currentMinutes.toString(),
            (value) => {
                const minutes = parseInt(value, 10);
                if (isNaN(minutes) || minutes < 0 || minutes > 999) {
                    return 'Please enter valid minutes (0-999)';
                }
                if (minutes === 0) {
                    return 'Match duration must be greater than 0';
                }
                return null;
            }
        );

        if (input === null) return;
        
        const minutes = parseInt(input, 10);
        const oldValue = this.matchDurationSeconds;
        const newValue = minutes * 60;
        
        this.matchDurationSeconds = newValue;
        this.matchRemainingSeconds = newValue;
        this.updateTimerDisplay();
        
        // Log timer change
        if (this.logger) {
            this.logger.log(
                this.logger.EVENT_TYPES.MATCH_TIMER_CHANGED,
                `Match duration changed from ${Math.floor(oldValue / 60)} to ${minutes} minutes`,
                null,
                { oldValue, newValue }
            );
        }
        
        this.saveDebounced();
    }

    // Edit countdown preset via custom modal
    async editCountdownPreset() {
        const currentMinutes = Math.floor(this.countdownPreset / 60);
        const currentSeconds = this.countdownPreset % 60;

        const input = await this.showModal(
            'Enter rotation countdown (MM:SS):',
            `${currentMinutes}:${currentSeconds.toString().padStart(2, '0')}`,
            (value) => {
                const parts = value.split(':');
                if (parts.length !== 2) {
                    return 'Please enter time in MM:SS format (e.g., 2:00)';
                }

                const minutes = parseInt(parts[0], 10);
                const seconds = parseInt(parts[1], 10);

                if (isNaN(minutes) || isNaN(seconds) || minutes < 0 || minutes > 99 || seconds < 0 || seconds > 59) {
                    return 'Please enter valid minutes (0-99) and seconds (0-59)';
                }

                const totalSeconds = minutes * 60 + seconds;
                if (totalSeconds === 0) {
                    return 'Countdown time must be greater than 0';
                }

                return null;
            },
            () => this.calculateOptimalRotationTime() // Auto button handler
        );

        if (input === null) return;

        const parts = input.split(':');
        const minutes = parseInt(parts[0], 10);
        const seconds = parseInt(parts[1], 10);
        const oldValue = this.countdownPreset;
        const totalSeconds = minutes * 60 + seconds;

        this.countdownPreset = totalSeconds;
        this.countdownRemaining = this.countdownPreset;
        this.updateCountdownDisplay();
        this.setRotateAttention(false);

        // Log rotation timer change
        if (this.logger) {
            this.logger.log(
                this.logger.EVENT_TYPES.ROTATION_TIMER_CHANGED,
                `Rotation countdown changed from ${Math.floor(oldValue / 60)}:${(oldValue % 60).toString().padStart(2, '0')} to ${minutes}:${seconds.toString().padStart(2, '0')}`,
                null,
                { oldValue, newValue: totalSeconds }
            );
        }

        if (this.timerRunning) {
            this.pauseCountdown();
            this.startCountdown();
        }

        this.saveDebounced();
    }

    // Calculate optimal rotation time for equal playing time
    calculateOptimalRotationTime() {
        // Count active field players (excluding goalie and inactive)
        const fieldPlayers = this.rows.filter(r => 
            r.cbField.checked && !r.cbInactive.checked && !r.cbGoalie.checked
        ).length;

        // Count active bench players (excluding goalie and inactive)
        const benchPlayers = this.rows.filter(r => 
            r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked
        ).length;

        // Validation
        if (fieldPlayers === 0) {
            this.modalError.textContent = 'No field players assigned';
            return null;
        }

        if (benchPlayers === 0) {
            this.modalError.textContent = 'No bench players to rotate';
            return null;
        }

        // FORMULA 1: Equal playing time based on bench size
        // rotation_time = (match_duration × rotation_count) / bench_players
        const equalTimeRotation = Math.round(
            (this.matchDurationSeconds * this.rotationCount) / benchPlayers
        );

        // FORMULA 2: Fast fives / high-frequency rotation mode (BENCH-AWARE)
        // Ensures minimum rotation frequency for fast games
        // BUT also ensures we rotate often enough to give everyone playing time
        // Example: 40-min game, 10 bench players, rotate 1
        //   - Need at least 10 rotations to cycle through everyone
        //   - targetRotationsPerHalf = 5 → need at least 10 total rotations
        //   - minRotationsPerHalf = max(5, 10/1) = 10
        //   - rotation_time = (1200 × 1) / 10 = 120 sec = 2 min
        const halfDuration = this.matchDurationSeconds / 2;
        const targetRotationsPerHalf = 5; // Baseline for fast games
        const minRotationsPerHalf = Math.max(
            targetRotationsPerHalf,
            Math.ceil(benchPlayers / this.rotationCount)  // At least 1 full cycle
        );
        const fastFivesRotation = Math.round(
            (halfDuration * this.rotationCount) / minRotationsPerHalf
        );

        // Use the SMALLER value (more frequent rotations)
        // This ensures fast fives get frequent rotations, while longer matches
        // still respect bench size for equal playing time
        const rotationTimeSeconds = Math.min(equalTimeRotation, fastFivesRotation);

        if (rotationTimeSeconds <= 0) {
            this.modalError.textContent = 'Calculated time is too short';
            return null;
        }

        const minutes = Math.floor(rotationTimeSeconds / 60);
        const seconds = rotationTimeSeconds % 60;

        return `${minutes}:${seconds.toString().padStart(2, '0')}`;
    }

    // Show rotation count modal
    showRotationCountModal() {
        this.updateRotationCountMax();
        this.updateRotationCountDisplay();
        this.rotationModalOverlay.classList.add('active');
    }

    // Hide rotation count modal
    hideRotationCountModal() {
        this.rotationModalOverlay.classList.remove('active');
        this.updateRotateButtonText(); // Update button text when modal closes
    }

    // Update rotation count display and button states
    updateRotationCountDisplay() {
        this.rotationCountDisplay.textContent = this.rotationCount.toString();

        // Update button states
        const benchCount = this.rows.filter(r => 
            r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked
        ).length;

        this.rotationDecrement.disabled = this.rotationCount <= 1;
        this.rotationIncrement.disabled = this.rotationCount >= benchCount || benchCount === 0;

        // Update rotate button text
        this.updateRotateButtonText();

        // Update player highlighting to reflect new rotation count
        this.markNextPlayers();

        // Update rotation display if in View_D mode
        if (this.viewMode === 2) {
            this.updateRotationDisplay();
        }
    }

    // Update max rotation count based on bench players
    updateRotationCountMax() {
        const benchCount = this.rows.filter(r => 
            r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked
        ).length;

        if (this.rotationCount > benchCount && benchCount > 0) {
            this.rotationCount = benchCount;
        }
        if (this.rotationCount < 1) {
            this.rotationCount = 1;
        }
    }

    // Increment rotation count
    incrementRotationCount() {
        const benchCount = this.rows.filter(r => 
            r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked
        ).length;

        if (this.rotationCount < benchCount) {
            this.rotationCount++;
            this.updateRotationCountDisplay();
        }
    }

    // Decrement rotation count
    decrementRotationCount() {
        if (this.rotationCount > 1) {
            this.rotationCount--;
            this.updateRotationCountDisplay();
        }
    }

    // Update rotate button text with rotation count
    updateRotateButtonText() {
        this.rotateBtn.textContent = `Rotate ${this.rotationCount}`;
    }

    // Update view button state based on player selection
    updateViewButtonState() {
        // Check if any players are assigned to field, bench, or goalie
        const hasSelectedPlayers = this.rows.some(r => 
            r.cbField.checked || r.cbBench.checked || r.cbGoalie.checked
        );

        // Disable view button if no players are selected
        this.viewlessBtn.disabled = !hasSelectedPlayers;
    }

    // Update start button state based on field player assignment
    updateStartButtonState() {
        // Only manage button state during setup phase
        // Once game starts, button should remain enabled for pause/resume
        if (this.currentHalf !== 'setup') {
            this.startBtn.disabled = false; // Ensure button is enabled after setup
            return;
        }

        // During setup: Check if any players are assigned to field or goalie (players on the field)
        const hasFieldPlayers = this.rows.some(r => 
            r.cbField.checked || r.cbGoalie.checked
        );

        // Disable start button if no field players are assigned
        this.startBtn.disabled = !hasFieldPlayers;
    }

    // Score tracking methods
    incrementTeamAScore() {
        this.teamAScore++;
        this.updateScoreDisplays();
        this.saveDebounced();
    }

    incrementTeamBScore() {
        this.teamBScore++;
        this.updateScoreDisplays();
        this.saveDebounced();
    }

    updateScoreDisplays() {
        // Update View_D button scores
        if (this.teamAScoreDisplay) {
            this.teamAScoreDisplay.textContent = this.teamAScore.toString();
        }
        if (this.teamBScoreDisplay) {
            this.teamBScoreDisplay.textContent = this.teamBScore.toString();
        }

        // Update header scores
        if (this.headerScoreUsValue) {
            this.headerScoreUsValue.textContent = this.teamAScore.toString();
        }
        if (this.headerScoreThemValue) {
            this.headerScoreThemValue.textContent = this.teamBScore.toString();
        }
    }

    resetScores() {
        this.teamAScore = 0;
        this.teamBScore = 0;
        this.updateScoreDisplays();
        this.updateScoreVisibility();
    }

    updateScoreVisibility() {
        // Show scores in header only when game has started
        const showScores = this.currentHalf !== 'setup';

        if (this.headerScoreUs) {
            this.headerScoreUs.style.display = showScores ? '' : 'none';
        }
        if (this.headerScoreThem) {
            this.headerScoreThem.style.display = showScores ? '' : 'none';
        }
    }

    // Timer logic (countdown with half-time support)
    toggleStartPause() {
        // When game has ended, allow pause but not resume
        if (this.currentHalf === 'end') {
            if (this.timerRunning) {
                this.pauseTimer();
            }
            // Don't allow resuming after game ends - only restart works
            return;
        }
        
        if (this.currentHalf === 'halftime') {
            if (this.timerRunning) {
                this.pauseTimer();
            }
            
            // Log halftime when user actually clicks the button
            if (this.logger) {
                this.logger.log(
                    this.logger.EVENT_TYPES.HALF_TIME,
                    `Half-time - 1st half time: ${this.formatTimeForLog(this.matchRemainingSeconds)}`,
                    null,
                    {}
                );
            }
            
            this.currentHalf = '2nd';
            this.matchRemainingSeconds = this.halfDurationSeconds;
            this.updateTimerDisplay();
            this.updateMatchTimeLabel();
            this.startBtn.textContent = 'Resume';
            this.timerLabel.style.cursor = 'pointer';
            
            // Log second half start
            if (this.logger) {
                this.logger.log(
                    this.logger.EVENT_TYPES.SECOND_HALF_STARTED,
                    'Second half started',
                    null,
                    {}
                );
            }
            
            this.saveDebounced();
            return;
        }
        
        if (this.timerRunning) {
            this.pauseTimer();
        } else {
            this.startTimer();
        }
    }
    
    startTimer() {
        if (this.timerRunning) return;

        if (!this.initialArrangementDone && this.currentHalf === 'setup') {
            this.applyInitialArrangement();
            this.initialArrangementDone = true;
            this.currentHalf = '1st';
            this.halfDurationSeconds = this.matchDurationSeconds / 2;
            this.matchRemainingSeconds = this.halfDurationSeconds;
            this.updateMatchTimeLabel();
            this.updateScoreVisibility(); // Show scores when game starts

            // Update table inactive rows when game starts
            this.updateTableInactiveRows();
            // Rebuild swipeable roster to show inactive toggle
            if (this.viewMode === 0) {
                this.buildSwipeableRoster();
            }

            // Start new game session
            if (this.logger) {
                this.logger.startSession(
                    this.matchDurationSeconds,
                    this.countdownPreset,
                    null // Location can be added later
                );
            }

            this.saveDebounced();
        } else {
            // Game resumed
            if (this.logger) {
                this.logger.log(
                    this.logger.EVENT_TYPES.GAME_RESUMED,
                    'Match resumed',
                    null,
                    {}
                );
            }
        }

        // Hide inactive players by default after game starts
        if (this.currentHalf !== 'setup' && this.viewMode === 0) {
            this.showInactivePlayers = false;
            this.buildSwipeableRoster();
        }

        this.timerRunning = true;
        this.startBtn.textContent = 'Pause';
        this.timerLabel.style.cursor = 'not-allowed';

        if (this.countdownRemaining <= 0) {
            this.countdownRemaining = this.countdownPreset;
            this.updateCountdownDisplay();
            this.setRotateAttention(false);
        }
        this.timerId = setInterval(() => {
            this.matchRemainingSeconds -= 1;
            this.updateTimerDisplay();
            
            if (this.matchRemainingSeconds === 0 && this.currentHalf === '1st') {
                this.currentHalf = 'halftime';
                this.startBtn.textContent = '1/2 Time';
                
                // Don't log halftime yet - will be logged when user clicks button
                this.saveDebounced();
            }
            else if (this.matchRemainingSeconds === 0 && this.currentHalf === '2nd') {
                this.currentHalf = 'end';
                this.startBtn.textContent = 'End';
                
                // Don't end session yet - let timer continue for overtime
                // Session will be ended when user restarts or closes app
                if (this.logger) {
                    this.logger.log(
                        this.logger.EVENT_TYPES.GAME_ENDED,
                        'Regulation time ended',
                        null,
                        {}
                    );
                }
                
                this.saveDebounced();
            }
        }, 1000);
        this.startCountdown();
        this.rows.forEach(r => {
            if (r.cbField.checked || r.cbGoalie.checked) this.startCounter(r);
        });
    }
    
    pauseTimer() {
        if (!this.timerRunning) return;
        this.timerRunning = false;
        
        // Log pause event
        if (this.logger) {
            this.logger.log(
                this.logger.EVENT_TYPES.GAME_PAUSED,
                'Match paused',
                null,
                {}
            );
        }
        
        if (this.currentHalf === 'halftime') {
            this.startBtn.textContent = '1/2 Time';
        } else if (this.currentHalf === 'end') {
            this.startBtn.textContent = 'Reset';
        } else {
            this.startBtn.textContent = this.currentHalf === 'setup' ? 'Start' : 'Resume';
        }
        
        if (this.currentHalf === 'setup') {
            this.timerLabel.style.cursor = 'pointer';
        } else {
            this.timerLabel.style.cursor = 'not-allowed';
        }
        
        clearInterval(this.timerId);
        this.timerId = null;
        this.pauseCountdown();
        this.rows.forEach(r => this.stopCounter(r));
    }
    
    updateTimerDisplay() {
        const m = Math.floor(Math.abs(this.matchRemainingSeconds) / 60);
        const s = Math.abs(this.matchRemainingSeconds) % 60;
        const two = (n) => n.toString().padStart(2, '0');
        const sign = this.matchRemainingSeconds < 0 ? '-' : '';
        
        if (!this.timerRunning && s === 0 && this.currentHalf === 'setup') {
            this.timerLabel.textContent = `${m} min`;
        } else {
            this.timerLabel.textContent = `${sign}${two(m)}:${two(s)}`;
        }
        
        this.updateNameInputsEditability();
    }
    
    updateNameInputsEditability() {
        const locked = this.currentHalf !== 'setup';
        this.rows.forEach(r => {
            const shouldLock = locked && !r.cbInactive.checked;
            
            if (r.nameInput.readOnly !== shouldLock) {
                r.nameInput.readOnly = shouldLock;
                if (shouldLock) {
                    r.nameInput.setAttribute('title', 'Name editing is locked once match starts');
                } else {
                    r.nameInput.removeAttribute('title');
                }
            }
        });
    }

    restartGame() {
        // End current session before restarting
        if (this.logger && this.logger.currentSession) {
            // Log final game state before ending
            this.logger.log(
                this.logger.EVENT_TYPES.GAME_RESTARTED,
                `Match restarted - Final time: ${this.formatTimeForLog(this.matchRemainingSeconds)}`,
                null,
                {}
            );
            this.logger.endSession();
        }

        // Stop timer directly without logging pause event
        this.timerRunning = false;
        if (this.timerId) {
            clearInterval(this.timerId);
            this.timerId = null;
        }
        this.pauseCountdown();
        this.rows.forEach(r => this.stopCounter(r));

        // Reset game state
        this.halfDurationSeconds = 0;
        this.matchRemainingSeconds = this.matchDurationSeconds;
        this.updateTimerDisplay();
        this.countdownRemaining = this.countdownPreset;
        this.updateCountdownDisplay();
        this.setRotateAttention(false);
        this.lastFieldIdx = -1;
        this.lastBenchIdx = -1;
        this.initialArrangementDone = false;
        this.currentHalf = 'setup';
        this.rows.forEach(r => {
            r.counterSeconds = 0;
            this.updateCounterDisplay(r);
        });
        this.startBtn.textContent = 'Start';
        this.updateMatchTimeLabel();
        this.timerLabel.style.cursor = 'pointer';
        this.markNextPlayers();
        this.resetScores(); // Reset scores when restarting game
        this.updateStartButtonState(); // Ensure button state is correct for setup phase
        this.updateScoreVisibility(); // Hide scores during setup
        this.updateTableInactiveRows(); // Reset inactive toggle for table view
        // Rebuild swipeable roster to reset inactive toggle
        if (this.viewMode === 0) {
            this.buildSwipeableRoster();
        }
        this.saveDebounced();
    }
    
    // Helper to format time for logging
    formatTimeForLog(seconds) {
        const sign = seconds < 0 ? '-' : '';
        const abs = Math.abs(seconds);
        const m = Math.floor(abs / 60);
        const s = abs % 60;
        return `${sign}${m}:${s.toString().padStart(2, '0')}`;
    }

    startCountdown() {
        if (this.countdownId || this.countdownRemaining <= 0) return;
        this.countdownId = setInterval(() => {
            if (this.countdownRemaining > 0) {
                this.countdownRemaining -= 1;
                this.updateCountdownDisplay();
                
                if (this.countdownRemaining === 0) {
                    this.setRotateAttention(true);
                    this.pauseCountdown();
                }
            }
        }, 1000);
    }

    pauseCountdown() {
        if (this.countdownId) {
            clearInterval(this.countdownId);
            this.countdownId = null;
        }
    }

    resetCountdown(continueRunning) {
        this.countdownRemaining = this.countdownPreset;
        this.updateCountdownDisplay();
        this.setRotateAttention(false);
        this.pauseCountdown();
        if (continueRunning) this.startCountdown();
    }

    updateCountdownDisplay() {
        const m = Math.floor(this.countdownRemaining / 60);
        const s = this.countdownRemaining % 60;
        const two = (n) => n.toString().padStart(2, '0');
        this.countdownLabel.textContent = `${m}:${two(s)}`;
    }

    setRotateAttention(on) {
        this.rotateBtn.classList.toggle('rotate-attention', !!on);
        
        if (on) {
            document.body.classList.add('flash-yellow');
            this._flashTimeout = setTimeout(() => {
                document.body.classList.remove('flash-yellow');
            }, 5000);
        } else {
            document.body.classList.remove('flash-yellow');
            if (this._flashTimeout) {
                clearTimeout(this._flashTimeout);
                this._flashTimeout = null;
            }
        }
    }

    startCounter(row) {
        if (row.counterTimerId || !this.timerRunning) return;
        row.counterTimerId = setInterval(() => {
            row.counterSeconds++;
            this.updateCounterDisplay(row);
        }, 1000);
    }

    stopCounter(row) {
        if (row.counterTimerId) {
            clearInterval(row.counterTimerId);
            row.counterTimerId = null;
        }
    }

    updateCounterDisplay(row) {
        const m = Math.floor(row.counterSeconds / 60);
        const s = row.counterSeconds % 60;
        const timeText = `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
        row.counterDisplay.textContent = timeText;

        // Also update swipeable element counter if it exists
        if (row.swipeableElement) {
            const swipeableCounter = row.swipeableElement.querySelector('.player-counter');
            if (swipeableCounter) {
                swipeableCounter.textContent = timeText;
            }
        }
    }

    buildRows() {
        for (let i = 1; i <= 16; i++) {
            const tr = document.createElement('tr');

            const tdName = document.createElement('td');
            const nameWrapper = document.createElement('div');
            nameWrapper.className = 'name-wrapper';
            const nameInput = document.createElement('input');
            nameInput.type = 'text';
            nameInput.className = 'name-input player-none';
            nameInput.value = `Player ${i}`;
            nameInput.setAttribute('aria-label', `Player ${i} name`);
            const counterDisplay = document.createElement('span');
            counterDisplay.className = 'player-counter';
            counterDisplay.textContent = '00:00';
            nameWrapper.appendChild(nameInput);
            nameWrapper.appendChild(counterDisplay);
            tdName.appendChild(nameWrapper);
            tr.appendChild(tdName);

            const makeCbCell = (cls, label) => {
                const td = document.createElement('td');
                const cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.className = `cb ${cls}`;
                cb.setAttribute('aria-label', `${label} for Player ${i}`);
                td.appendChild(cb);
                return { td, cb };
            };

            const { td: tdField, cb: cbField } = makeCbCell('cb-field', 'Field');
            const { td: tdBench, cb: cbBench } = makeCbCell('cb-bench', 'Bench');
            const { td: tdGoalie, cb: cbGoalie } = makeCbCell('cb-goalie', 'Goalie');
            const { td: tdInactive, cb: cbInactive } = makeCbCell('cb-inactive', 'Inactive');

            tr.appendChild(tdField);
            tr.appendChild(tdBench);
            tr.appendChild(tdGoalie);
            tr.appendChild(tdInactive);

            this.rosterBody.appendChild(tr);

            const updatePlayerColor = () => {
                nameInput.classList.remove('player-field', 'player-bench', 'player-goalie', 'player-inactive', 'player-none');
                if (cbField.checked) nameInput.classList.add('player-field');
                else if (cbBench.checked) nameInput.classList.add('player-bench');
                else if (cbGoalie.checked) nameInput.classList.add('player-goalie');
                else if (cbInactive.checked) nameInput.classList.add('player-inactive');
                else nameInput.classList.add('player-none');
                tr.classList.toggle('inactive-row', !!cbInactive.checked);

                // Update position icon in table view
                const row = this.rows.find(r => r.nameInput === nameInput);
                if (row) {
                    const icon = this.getPositionIcon(row);
                    const currentValue = nameInput.value;
                    // Remove ALL icons at the start
                    const valueWithoutIcon = currentValue.replace(/^([⚽💺🥅❌]\s*)+/, '');
                    nameInput.value = icon ? `${icon} ${valueWithoutIcon}` : valueWithoutIcon;
                }

                this.markNextPlayers();
                this.updateNameInputsEditability();
                this.updateViewButtonState(); // Update view button state
                this.updateStartButtonState(); // Update start button state
                this.saveDebounced();
            };

            cbField.addEventListener('change', () => {
                if (cbField.checked) {
                    // Get previous position BEFORE unchecking other boxes
                    const previousPosition = cbBench.checked ? 'bench' : cbGoalie.checked ? 'goalie' : cbInactive.checked ? 'inactive' : 'none';
                    
                    cbBench.checked = false;
                    cbInactive.checked = false;
                    cbGoalie.checked = false;
                    
                    const row = this.rows.find(r => r.nameInput === nameInput);
                    if (this.timerRunning && row) this.startCounter(row);
                    
                    // Log position change
                    if (this.logger) {
                        this.logger.log(
                            this.logger.EVENT_TYPES.PLAYER_TO_FIELD,
                            `${nameInput.value} moved to field`,
                            nameInput.value,
                            { 
                                fromPosition: previousPosition,
                                toPosition: 'field',
                                playerIndex: i - 1
                            }
                        );
                    }
                } else {
                    const row = this.rows.find(r => r.nameInput === nameInput);
                    if (row) this.stopCounter(row);
                }
                updatePlayerColor();
            });

            cbBench.addEventListener('change', () => {
                if (cbBench.checked) {
                    // Get previous position BEFORE unchecking other boxes
                    const previousPosition = cbField.checked ? 'field' : cbGoalie.checked ? 'goalie' : cbInactive.checked ? 'inactive' : 'none';
                    
                    cbField.checked = false;
                    cbInactive.checked = false;
                    cbGoalie.checked = false;
                    
                    const row = this.rows.find(r => r.nameInput === nameInput);
                    if (row) this.stopCounter(row);
                    
                    // Log position change
                    if (this.logger) {
                        this.logger.log(
                            this.logger.EVENT_TYPES.PLAYER_TO_BENCH,
                            `${nameInput.value} moved to bench`,
                            nameInput.value,
                            { 
                                fromPosition: previousPosition,
                                toPosition: 'bench',
                                playerIndex: i - 1
                            }
                        );
                    }
                }
                updatePlayerColor();
            });

            cbInactive.addEventListener('change', () => {
                if (cbInactive.checked) {
                    // Get previous position BEFORE unchecking other boxes
                    const previousPosition = cbField.checked ? 'field' : cbBench.checked ? 'bench' : cbGoalie.checked ? 'goalie' : 'none';

                    cbField.checked = false;
                    cbBench.checked = false;
                    cbGoalie.checked = false;

                    const row = this.rows.find(r => r.nameInput === nameInput);
                    if (row) this.stopCounter(row);

                    // Log position change
                    if (this.logger) {
                        this.logger.log(
                            this.logger.EVENT_TYPES.PLAYER_TO_INACTIVE,
                            `${nameInput.value} moved to inactive`,
                            nameInput.value,
                            { 
                                fromPosition: previousPosition,
                                toPosition: 'inactive',
                                playerIndex: i - 1
                            }
                        );
                    }
                }
                updatePlayerColor();
                // Update table inactive toggle when inactive status changes
                this.updateTableInactiveRows();
            });

            cbGoalie.addEventListener('change', () => {
                if (cbGoalie.checked) {
                    // Get previous position BEFORE unchecking other boxes
                    const previousPosition = cbField.checked ? 'field' : cbBench.checked ? 'bench' : cbInactive.checked ? 'inactive' : 'none';
                    
                    this.rows.forEach(r => {
                        if (r.cbGoalie !== cbGoalie && r.cbGoalie.checked) {
                            r.cbGoalie.checked = false;
                            this.stopCounter(r);
                            r.updatePlayerColor();
                        }
                    });
                    
                    cbInactive.checked = false;
                    cbField.checked = false;
                    cbBench.checked = false;
                    
                    const row = this.rows.find(r => r.nameInput === nameInput);
                    if (this.timerRunning && row) this.startCounter(row);
                    
                    // Log position change
                    if (this.logger) {
                        this.logger.log(
                            this.logger.EVENT_TYPES.PLAYER_TO_GOALIE,
                            `${nameInput.value} moved to goalie`,
                            nameInput.value,
                            { 
                                fromPosition: previousPosition,
                                toPosition: 'goalie',
                                playerIndex: i - 1
                            }
                        );
                    }
                } else {
                    const row = this.rows.find(r => r.nameInput === nameInput);
                    if (row) this.stopCounter(row);
                }
                updatePlayerColor();
            });

            nameInput.addEventListener('input', () => {
                // Strip ALL icons when user is typing
                const row = this.rows.find(r => r.nameInput === nameInput);
                if (row && !nameInput.readOnly) {
                    let value = nameInput.value;
                    // Remove all icons if present
                    const cleanValue = value.replace(/^([⚽💺🥅❌]\s*)+/, '');
                    if (cleanValue !== value) {
                        const cursorPos = nameInput.selectionStart;
                        nameInput.value = cleanValue;
                        // Adjust cursor position
                        const removedChars = value.length - cleanValue.length;
                        nameInput.setSelectionRange(Math.max(0, cursorPos - removedChars), Math.max(0, cursorPos - removedChars));
                    }
                }
                this.saveDebounced();
            });

            nameInput.addEventListener('blur', () => {
                // Re-add icon after editing
                const row = this.rows.find(r => r.nameInput === nameInput);
                if (row) {
                    const icon = this.getPositionIcon(row);
                    let value = nameInput.value.trim();
                    // Remove ALL icons that might be present
                    value = value.replace(/^([⚽💺🥅❌]\s*)+/, '');
                    if (value) {
                        nameInput.value = icon ? `${icon} ${value}` : value;
                    }
                }
            });

            const handleSelectAsNext = (evt) => {
                if (this.elapsedSeconds <= 0) return;
                if (cbGoalie.checked || cbInactive.checked) return;
                const idx = this.rows.findIndex(r => r.nameInput === nameInput);
                if (idx === -1) return;
                if (cbField.checked) {
                    this.lastFieldIdx = (idx - 1 + this.rows.length) % this.rows.length;
                    this.markNextPlayers();
                    if (this.viewMode === 2) {
                        this.updateRotationDisplay();
                    }
                } else if (cbBench.checked) {
                    this.lastBenchIdx = (idx - 1 + this.rows.length) % this.rows.length;
                    this.markNextPlayers();
                    if (this.viewMode === 2) {
                        this.updateRotationDisplay();
                    }
                }
            };

            // Track touch/mouse movement to distinguish tap from scroll
            let touchStartX = 0;
            let touchStartY = 0;
            let touchStartTime = 0;
            let touchStartScrollTop = 0;
            let hasMoved = false;

            const handleTouchStart = (e) => {
                const touch = e.touches ? e.touches[0] : e;
                touchStartX = touch.clientX;
                touchStartY = touch.clientY;
                touchStartTime = Date.now();
                hasMoved = false;

                // Track parent scroll position
                const panel = nameInput.closest('.panel');
                touchStartScrollTop = panel ? panel.scrollTop : 0;
            };

            const handleTouchMove = (e) => {
                if (hasMoved) return;

                const touch = e.touches ? e.touches[0] : e;
                const deltaX = Math.abs(touch.clientX - touchStartX);
                const deltaY = Math.abs(touch.clientY - touchStartY);

                // If moved more than 10px, consider it a scroll
                if (deltaX > 10 || deltaY > 10) {
                    hasMoved = true;
                }
            };

            const handleTouchEnd = (e) => {
                const duration = Date.now() - touchStartTime;

                // Check if parent scrolled
                const panel = nameInput.closest('.panel');
                const currentScrollTop = panel ? panel.scrollTop : 0;
                const scrollChanged = Math.abs(currentScrollTop - touchStartScrollTop) > 2;

                // Only trigger selection if:
                // 1. Touch duration < 200ms (quick tap)
                // 2. No significant movement detected
                // 3. Parent container didn't scroll
                if (duration < 200 && !hasMoved && !scrollChanged) {
                    handleSelectAsNext(e);
                }
            };

            // Use touch events for touch devices, mouse events for desktop
            if ('ontouchstart' in window) {
                nameInput.addEventListener('touchstart', handleTouchStart, { passive: true });
                nameInput.addEventListener('touchmove', handleTouchMove, { passive: true });
                nameInput.addEventListener('touchend', handleTouchEnd);
            } else {
                // Desktop: use click event (no scroll conflict on desktop)
                nameInput.addEventListener('click', handleSelectAsNext);
            }

            updatePlayerColor();

            const row = {
                tr, nameInput, cbField, cbBench, cbGoalie, cbInactive,
                counterSeconds: 0, counterTimerId: null, counterDisplay, updatePlayerColor
            };
            this.rows.push(row);
            this.updateCounterDisplay(row);
            
            // Enable drag and drop for row reordering
            this.enableDragAndDrop(tr, row);
        }
    }

    enableDragAndDrop(tr, row) {
        // Make row draggable
        tr.draggable = true;
        tr.style.cursor = 'move';

        // Store event handlers on the row object so we can reference them (created only once)
        if (!row.dragHandlers) {
            row.dragHandlers = {
                dragstart: (e) => {
                    this.draggedRow = row;
                    tr.classList.add('dragging');
                    e.dataTransfer.effectAllowed = 'move';
                    e.dataTransfer.setData('text/html', tr.innerHTML);
                },
                
                dragend: (e) => {
                    tr.classList.remove('dragging');
                    this.rows.forEach(r => r.tr.classList.remove('drag-over'));
                    this.draggedRow = null;
                    this.dragOverRow = null;
                },
                
                dragover: (e) => {
                    if (e.preventDefault) {
                        e.preventDefault();
                    }
                    e.dataTransfer.dropEffect = 'move';
                    
                    if (this.draggedRow && this.draggedRow !== row) {
                        this.rows.forEach(r => r.tr.classList.remove('drag-over'));
                        tr.classList.add('drag-over');
                        this.dragOverRow = row;
                    }
                    return false;
                },
                
                dragenter: (e) => {
                    if (this.draggedRow && this.draggedRow !== row) {
                        tr.classList.add('drag-over');
                    }
                },
                
                dragleave: (e) => {
                    tr.classList.remove('drag-over');
                },
                
                drop: (e) => {
                    if (e.stopPropagation) {
                        e.stopPropagation();
                    }

                    if (this.draggedRow && this.dragOverRow && this.draggedRow !== this.dragOverRow) {
                        const draggedIndex = this.rows.indexOf(this.draggedRow);
                        const targetIndex = this.rows.indexOf(this.dragOverRow);

                        if (draggedIndex !== -1 && targetIndex !== -1) {
                            // Find which rows are currently marked as "next" before reordering
                            const benchCandidates = [];
                            const fieldCandidates = [];
                            this.rows.forEach((r, idx) => {
                                if (r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked) benchCandidates.push(idx);
                                if (r.cbField.checked && !r.cbInactive.checked && !r.cbGoalie.checked) fieldCandidates.push(idx);
                            });

                            // Get the current "next" rows before reordering
                            const nextFieldIdx = this._nextIndexFrom(fieldCandidates, this.lastFieldIdx);
                            const nextBenchIdx = this._nextIndexFrom(benchCandidates, this.lastBenchIdx);
                            const nextFieldRow = nextFieldIdx !== -1 ? this.rows[nextFieldIdx] : null;
                            const nextBenchRow = nextBenchIdx !== -1 ? this.rows[nextBenchIdx] : null;

                            // Perform the reorder
                            this.rows.splice(draggedIndex, 1);
                            const newTargetIndex = draggedIndex < targetIndex ? targetIndex : targetIndex;
                            this.rows.splice(newTargetIndex, 0, this.draggedRow);

                            // Rebuild DOM with proper drag/drop
                            this.rebuildDOM();

                            // Update pointers to point to the new positions of the same "next" rows
                            if (nextFieldRow) {
                                const newFieldIdx = this.rows.indexOf(nextFieldRow);
                                if (newFieldIdx !== -1) {
                                    // Set lastFieldIdx so that nextFieldRow is still the next one
                                    this.lastFieldIdx = (newFieldIdx - 1 + this.rows.length) % this.rows.length;
                                }
                            }
                            if (nextBenchRow) {
                                const newBenchIdx = this.rows.indexOf(nextBenchRow);
                                if (newBenchIdx !== -1) {
                                    // Set lastBenchIdx so that nextBenchRow is still the next one
                                    this.lastBenchIdx = (newBenchIdx - 1 + this.rows.length) % this.rows.length;
                                }
                            }

                            // Log player reordering
                            if (this.logger) {
                                this.logger.log(
                                    this.logger.EVENT_TYPES.PLAYER_REORDERED,
                                    `${this.draggedRow.nameInput.value} moved from position ${draggedIndex + 1} to ${newTargetIndex + 1}`,
                                    this.draggedRow.nameInput.value,
                                    {
                                        fromIndex: draggedIndex,
                                        toIndex: newTargetIndex
                                    }
                                );
                            }

                            this.markNextPlayers();
                            if (this.viewMode === 2) {
                                this.updateRotationDisplay();
                            }
                            this.saveDebounced();
                        }
                    }

                    tr.classList.remove('drag-over');
                    return false;
                }
            };
            
            // Attach event listeners only when first creating handlers
            tr.addEventListener('dragstart', row.dragHandlers.dragstart);
            tr.addEventListener('dragend', row.dragHandlers.dragend);
            tr.addEventListener('dragover', row.dragHandlers.dragover);
            tr.addEventListener('dragenter', row.dragHandlers.dragenter);
            tr.addEventListener('dragleave', row.dragHandlers.dragleave);
            tr.addEventListener('drop', row.dragHandlers.drop);
        }
    }

    // Helper method to rebuild DOM after reordering
    rebuildDOM() {
        this.rosterBody.innerHTML = '';
        this.rows.forEach(r => this.rosterBody.appendChild(r.tr));
    }

    applyInitialArrangement() {
        this.rows.forEach(r => {
            const hasPos = r.cbField.checked || r.cbBench.checked || r.cbGoalie.checked;
            if (!hasPos) {
                r.cbInactive.checked = true;
                r.cbField.checked = false;
                r.cbBench.checked = false;
                r.cbGoalie.checked = false;
                r.updatePlayerColor();
            }
        });

        const field = [], bench = [], goalie = [], inactive = [];
        this.rows.forEach(r => {
            if (r.cbField.checked && !r.cbInactive.checked && !r.cbGoalie.checked) field.push(r);
            else if (r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked) bench.push(r);
            else if (r.cbGoalie.checked && !r.cbInactive.checked) goalie.push(r);
            else inactive.push(r);
        });

        // NEW ORDER: Field ? Goalie ? Bench ? Inactive
        const ordered = [...field, ...goalie, ...bench, ...inactive];
        this.rows = ordered;
        this.rebuildDOM();

        this.markNextPlayers();
    }

    rotateOnce() {
        const benchCandidates = [];
        const fieldCandidates = [];
        this.rows.forEach((r, idx) => {
            if (r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked) benchCandidates.push(idx);
            if (r.cbField.checked && !r.cbInactive.checked && !r.cbGoalie.checked) fieldCandidates.push(idx);
        });

        if (benchCandidates.length === 0 || fieldCandidates.length === 0) return;

        const fieldIdx = this._nextIndexFrom(fieldCandidates, this.lastFieldIdx);
        const benchIdx = this._nextIndexFrom(benchCandidates, this.lastBenchIdx);
        if (fieldIdx === -1 || benchIdx === -1 || benchIdx === fieldIdx) return;

        const benchRow = this.rows[benchIdx];
        const fieldRow = this.rows[fieldIdx];

        // Log rotation BEFORE swapping
        if (this.logger) {
            const rotationCount = this.logger.currentSession ? 
                this.logger.currentSession.logs.filter(l => l.eventType === this.logger.EVENT_TYPES.ROTATION_EXECUTED).length + 1 : 1;
            
            this.logger.log(
                this.logger.EVENT_TYPES.ROTATION_EXECUTED,
                `Rotation #${rotationCount}: ${fieldRow.nameInput.value} OFF, ${benchRow.nameInput.value} ON`,
                null,
                {
                    playerOut: fieldRow.nameInput.value,
                    playerIn: benchRow.nameInput.value,
                    rotationNumber: rotationCount
                }
            );
        }

        // Swap the checkbox states
        benchRow.cbBench.checked = false;
        benchRow.cbField.checked = true;
        benchRow.updatePlayerColor();

        fieldRow.cbField.checked = false;
        fieldRow.cbBench.checked = true;
        fieldRow.updatePlayerColor();

        // Physically swap the rows in the array
        const temp = this.rows[fieldIdx];
        this.rows[fieldIdx] = this.rows[benchIdx];
        this.rows[benchIdx] = temp;

        // Rebuild the DOM to reflect the new order
        this.rebuildDOM();

        // Start/stop counters based on new positions
        if (this.timerRunning) {
            this.startCounter(this.rows[fieldIdx]); // benchRow is now at fieldIdx
            this.stopCounter(this.rows[benchIdx]);   // fieldRow is now at benchIdx
        }

        // Update last indices - since we swapped, the indices stay the same
        this.lastFieldIdx = fieldIdx;
        this.lastBenchIdx = benchIdx;

        this.markNextPlayers();

        this.saveDebounced();
    }

    // Helper method to get current player position
    getPlayerPosition(row) {
        if (row.cbField.checked) return 'field';
        if (row.cbBench.checked) return 'bench';
        if (row.cbGoalie.checked) return 'goalie';
        if (row.cbInactive.checked) return 'inactive';
        return 'none';
    }

    saveToStorage() {
        try {
            const model = {
                version: this.STORAGE_VERSION,
                lastModifiedUtc: new Date().toISOString(),
                matchDurationSeconds: this.matchDurationSeconds,
                halfDurationSeconds: this.halfDurationSeconds,
                matchRemainingSeconds: this.matchRemainingSeconds,
                currentHalf: this.currentHalf,
                countdownPreset: this.countdownPreset,
                viewMode: this.viewMode, // Save view mode
                teamAScore: this.teamAScore,
                teamBScore: this.teamBScore,
                players: this.rows.map(r => ({
                    name: r.nameInput.value.replace(/^([⚽💺🥅❌]\s*)+/, ''), // Strip ALL icons before saving
                    field: !!r.cbField.checked,
                    bench: !!r.cbBench.checked,
                    goalie: !!r.cbGoalie.checked,
                    inactive: !!r.cbInactive.checked,
                    counterSeconds: r.counterSeconds
                }))
            };
            localStorage.setItem(this.STORAGE_KEY, JSON.stringify(model));
        } catch (error) {
            console.error('[RosterManager] Error saving to storage:', error);
        }
    }

    loadFromStorage() {
        try {
            const raw = localStorage.getItem(this.STORAGE_KEY);
            if (!raw) return;
            const model = JSON.parse(raw);
            
            // Check version and clear if incompatible
            if (!model || !Array.isArray(model.players) || model.version !== this.STORAGE_VERSION) {
                console.log('[RosterManager] Storage version mismatch or invalid data, clearing');
                localStorage.removeItem(this.STORAGE_KEY);
                return;
            }
            
            if (typeof model.matchDurationSeconds === 'number') {
                this.matchDurationSeconds = model.matchDurationSeconds;
            }
            if (typeof model.halfDurationSeconds === 'number') {
                this.halfDurationSeconds = model.halfDurationSeconds;
            }
            if (typeof model.matchRemainingSeconds === 'number') {
                this.matchRemainingSeconds = model.matchRemainingSeconds;
            }
            if (typeof model.currentHalf === 'string') {
                this.currentHalf = model.currentHalf;
            }
            if (typeof model.countdownPreset === 'number') {
                this.countdownPreset = model.countdownPreset;
                this.countdownRemaining = this.countdownPreset;
            }
            if (typeof model.teamAScore === 'number') {
                this.teamAScore = model.teamAScore;
            }
            if (typeof model.teamBScore === 'number') {
                this.teamBScore = model.teamBScore;
            }
            this.updateScoreDisplays();
            this.updateScoreVisibility();

            if (typeof model.viewMode === 'number' && model.viewMode >= 0 && model.viewMode <= 2) {
                this.viewMode = model.viewMode;
                // View will be initialized by initializeView() after loadFromStorage completes
            }
            this.updateTimerDisplay();
            this.updateCountdownDisplay();
            this.updateMatchTimeLabel();
            model.players.forEach((p, i) => {
                const r = this.rows[i];
                if (!r) return;
                if (typeof p.name === 'string') r.nameInput.value = p.name;
                r.cbField.checked = !!p.field;
                r.cbBench.checked = !!p.bench;
                r.cbGoalie.checked = !!p.goalie;
                r.cbInactive.checked = !!p.inactive;
                if (typeof p.counterSeconds === 'number') r.counterSeconds = p.counterSeconds;
                r.updatePlayerColor();
                this.updateCounterDisplay(r);
            });
        } catch (error) {
            console.error('[RosterManager] Error loading from storage:', error);
            localStorage.removeItem(this.STORAGE_KEY);
        }
    }

    markNextPlayers() {
        // Remove all rotation style classes first
        this.rows.forEach(r => {
            r.tr.classList.remove('player-next');
            for (let i = 1; i <= 5; i++) {
                r.tr.classList.remove(`rotate-style-${i}`);
            }
        });

        const benchCandidates = [];
        const fieldCandidates = [];
        this.rows.forEach((r, idx) => {
            if (r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked) benchCandidates.push(idx);
            if (r.cbField.checked && !r.cbInactive.checked && !r.cbGoalie.checked) fieldCandidates.push(idx);
        });

        // Mark rotationCount number of players for rotation
        const rotations = Math.min(this.rotationCount, benchCandidates.length, fieldCandidates.length);

        for (let i = 0; i < rotations; i++) {
            const nextFieldIdx = this._nextIndexFromWithOffset(fieldCandidates, this.lastFieldIdx, i);
            const nextBenchIdx = this._nextIndexFromWithOffset(benchCandidates, this.lastBenchIdx, i);

            if (nextFieldIdx !== -1) {
                this.rows[nextFieldIdx].tr.classList.add('player-next');
                this.rows[nextFieldIdx].tr.classList.add(`rotate-style-${this.rotationStyle}`);
            }
            if (nextBenchIdx !== -1) {
                this.rows[nextBenchIdx].tr.classList.add('player-next');
                this.rows[nextBenchIdx].tr.classList.add(`rotate-style-${this.rotationStyle}`);
            }
        }

        // Also mark in swipeable view if active
        this.markNextPlayersSwipeable();
    }

    _nextIndexFrom(candidates, lastIdx) {
        if (candidates.length === 0) return -1;
        for (let i = 1; i <= this.rows.length; i++) {
            const probe = (lastIdx + i) % this.rows.length;
            if (candidates.includes(probe)) return probe;
        }
        return -1;
    }

    _nextIndexFromWithOffset(candidates, lastIdx, offset) {
        if (candidates.length === 0) return -1;
        for (let i = 1; i <= this.rows.length; i++) {
            const probe = (lastIdx + i) % this.rows.length;
            if (candidates.includes(probe)) {
                // Found the starting position, now apply offset
                const startPos = candidates.indexOf(probe);
                const targetPos = (startPos + offset) % candidates.length;
                return candidates[targetPos];
            }
        }
        return -1;
    }

    _debounce(fn, ms) {
        let t = null;
        return (...args) => {
            if (t) clearTimeout(t);
            t = setTimeout(() => fn.apply(this, args), ms);
        };
    }

    loadRotationStyle() {
        const saved = localStorage.getItem('rotation_style');
        if (saved) {
            const styleNum = parseInt(saved, 10);
            if (styleNum >= 1 && styleNum <= 5) {
                this.rotationStyle = styleNum;
            }
        }
    }

    setRotationStyle(styleNum) {
        if (styleNum >= 1 && styleNum <= 5) {
            this.rotationStyle = styleNum;
            localStorage.setItem('rotation_style', styleNum.toString());
            this.markNextPlayers(); // Re-apply highlighting with new style
        }
    }

    loadTeamViewPreference() {
        const saved = localStorage.getItem('team_view_preference');
        if (saved && (saved === 'swipe' || saved === 'table')) {
            this.preferredTeamView = saved;
        } else {
            this.preferredTeamView = 'swipe'; // Default
        }
    }

    setTeamViewPreference(viewType) {
        if (viewType === 'swipe' || viewType === 'table') {
            this.preferredTeamView = viewType;
            localStorage.setItem('team_view_preference', viewType);

            // Switch to the newly preferred view
            const targetMode = viewType === 'swipe' ? 0 : 1;
            if (this.viewMode !== targetMode && this.viewMode !== 2) {
                // If we're not in rotation view, switch to the new preferred view
                this.switchToView(targetMode);
            }
        }
    }

    switchToView(targetMode) {
        this.viewMode = targetMode;

        // Update body classes
        document.body.classList.remove('less-view', 'min-view', 'rotation-view');
        const panel = document.querySelector('.panel');
        const rotationDisplay = document.getElementById('rotationDisplay');
        const swipeableRoster = this.swipeableRoster;

        if (targetMode === 0) {
            // Swipeable view
            this.viewlessBtn.textContent = 'VIEW_A';
            panel.style.display = 'none';
            rotationDisplay.style.display = 'none';
            swipeableRoster.style.display = '';
            this.buildSwipeableRoster();
        } else if (targetMode === 1) {
            // Table view
            document.body.classList.add('less-view');
            this.viewlessBtn.textContent = 'VIEW_B';
            panel.style.display = '';
            rotationDisplay.style.display = 'none';
            swipeableRoster.style.display = 'none';
            this.updateTableInactiveRows();
        } else if (targetMode === 2) {
            // Rotation view
            document.body.classList.add('rotation-view');
            this.viewlessBtn.textContent = 'VIEW_C';
            panel.style.display = 'none';
            rotationDisplay.style.display = '';
            swipeableRoster.style.display = 'none';
            this.updateRotationDisplay();
        }

        // Update inactive row visibility
        this.rows.forEach(r => r.tr.classList.toggle('inactive-row', !!r.cbInactive.checked));
        this.updateTableInactiveRows();
        this.updateDynamicSizing();
        setTimeout(() => this.updateDynamicSizing(), 100);
    }
}

// Theme switching function
function setTheme(theme) {
    const stylesheet = document.getElementById('theme-stylesheet');
    if (theme === 'modern') {
        stylesheet.href = 'css/styles-modern.css';
    } else {
        stylesheet.href = 'css/styles-classic.css';
    }
    localStorage.setItem('appTheme', theme);
}

// Load saved theme on page load
function loadSavedTheme() {
    const savedTheme = localStorage.getItem('appTheme') || 'classic';
    const stylesheet = document.getElementById('theme-stylesheet');
    if (savedTheme === 'modern') {
        stylesheet.href = 'css/styles-modern.css';
    } else {
        stylesheet.href = 'css/styles-classic.css';
    }
}

// Global roster manager instance
let rosterManagerInstance = null;

// Function callable from MAUI to set rotation style
function setRotationStyleFromMAUI(styleNum) {
    if (rosterManagerInstance) {
        rosterManagerInstance.setRotationStyle(styleNum);
    }
}

// Function callable from MAUI to set team view preference
function setTeamViewFromMAUI(viewType) {
    if (rosterManagerInstance) {
        rosterManagerInstance.setTeamViewPreference(viewType);
    }
}

// Initialize when DOM is ready
window.addEventListener('DOMContentLoaded', () => {
    loadSavedTheme();
    rosterManagerInstance = new RosterManager();
});

// Make functions available globally for MAUI to call
window.setRotationStyleFromMAUI = setRotationStyleFromMAUI;
window.setTeamViewFromMAUI = setTeamViewFromMAUI;
