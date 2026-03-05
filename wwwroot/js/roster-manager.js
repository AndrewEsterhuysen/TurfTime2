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

        // Modal elements
        this.modalOverlay = document.getElementById('modalOverlay');
        this.modalTitle = document.getElementById('modalTitle');
        this.modalInput = document.getElementById('modalInput');
        this.modalError = document.getElementById('modalError');
        this.modalOk = document.getElementById('modalOk');
        this.modalCancel = document.getElementById('modalCancel');

        // Rotation count modal elements
        this.rotationModalOverlay = document.getElementById('rotationModalOverlay');
        this.rotationCountDisplay = document.getElementById('rotationCountDisplay');
        this.rotationIncrement = document.getElementById('rotationIncrement');
        this.rotationDecrement = document.getElementById('rotationDecrement');
        this.rotationModalClose = document.getElementById('rotationModalClose');

        // Rotation count state
        this.rotationCount = 1;

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

        this.viewMode = 0; // 0=Standard, 1=Less, 2=Min
        this.isEditingName = false; // skip sizing updates while editing a name

        // Drag and drop state
        this.draggedRow = null;
        this.dragOverRow = null;

        // Initialize game logger
        this.logger = new GameLogger();
        this.logger.setRosterManager(this);

        this.buildRows();
        this.loadFromStorage();
        this.bindEvents();
        this.markNextPlayers();
        this.updateRotateButtonText(); // Initialize button text with rotation count

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
            this.updateDynamicSizing();
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

    // Toggle Less/More view
    toggleLessView() {
        // Cycle through: 0 (Standard) -> 1 (Less) -> 2 (Min) -> 0
        this.viewMode = (this.viewMode + 1) % 3;
        
        // Update body classes
        document.body.classList.remove('less-view', 'min-view');
        if (this.viewMode === 1) {
            document.body.classList.add('less-view');
            this.viewlessBtn.textContent = 'zoom';
        } else if (this.viewMode === 2) {
            document.body.classList.add('less-view', 'min-view');
            this.viewlessBtn.textContent = 'MIN';
        } else {
            this.viewlessBtn.textContent = 'ZOOM';
        }
        
        // Update inactive row visibility
        this.rows.forEach(r => r.tr.classList.toggle('inactive-row', !!r.cbInactive.checked));
        
        // Apply min view field player visibility if entering min view
        if (this.viewMode === 2) {
            this.updateMinViewVisibility();
        } else {
            // Clear min-hidden class when leaving min view
            this.rows.forEach(r => r.tr.classList.remove('min-hidden'));
        }
        
        this.updateDynamicSizing();
        setTimeout(() => this.updateDynamicSizing(), 100);
    }

    // Compute dynamic sizes for Less view based on viewport and visible rows
    updateDynamicSizing() {
        if (this.isEditingName) return;
        if (this.viewMode === 0) { // Standard view
            const b = document.body.style;
            b.removeProperty('--row-h');
            b.removeProperty('--cell-pad-v');
            b.removeProperty('--cell-pad-h');
            b.removeProperty('--input-h');
            b.removeProperty('--input-fs');
            b.removeProperty('--cb-scale');
            b.removeProperty('--btn-font');
            b.removeProperty('--btn-pad-v');
            b.removeProperty('--btn-pad-h');
            b.removeProperty('--timer-fs');
            b.removeProperty('--timer-pad-v');
            b.removeProperty('--timer-pad-h');
            return;
        }
        // Less view (1) and Min view (2) use same dynamic sizing
        const visibleRows = this.rows.filter(r => !r.cbInactive.checked && !r.tr.classList.contains('min-hidden'));
        const count = visibleRows.length || 1;
        const tbody = this.rosterBody;
        const table = tbody.closest('table');
        const container = document.querySelector('.container');

        const tbodyTop = tbody.getBoundingClientRect().top;
        const bottomControls = document.querySelector('.bottom-controls');
        const bottomH = bottomControls ? bottomControls.getBoundingClientRect().height : 0;
        const containerStyle = getComputedStyle(container);
        const paddingBottom = parseFloat(containerStyle.paddingBottom || '0');
        
        const thead = table.querySelector('thead');
        const theadH = thead ? thead.getBoundingClientRect().height : 30;
        const bufferSpace = 20;
        
        const available = Math.max(0, window.innerHeight - tbodyTop - bottomH - paddingBottom - bufferSpace);
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

    // Custom modal dialog helper
    showModal(title, defaultValue, validator) {
        return new Promise((resolve) => {
            this.modalTitle.textContent = title;
            this.modalInput.value = defaultValue;
            this.modalError.textContent = '';
            this.modalOverlay.classList.add('active');
            this.modalInput.focus();
            this.modalInput.select();

            const cleanup = () => {
                this.modalOverlay.classList.remove('active');
                this.modalOk.removeEventListener('click', okHandler);
                this.modalCancel.removeEventListener('click', cancelHandler);
                this.modalInput.removeEventListener('keydown', keyHandler);
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

            this.modalOk.addEventListener('click', okHandler);
            this.modalCancel.addEventListener('click', cancelHandler);
            this.modalInput.addEventListener('keydown', keyHandler);
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
            }
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
        row.counterDisplay.textContent = `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
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
                this.markNextPlayers();
                this.updateNameInputsEditability();
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
                this.saveDebounced();
            });

            const handleSelectAsNext = (evt) => {
                if (this.elapsedSeconds <= 0) return;
                if (cbGoalie.checked || cbInactive.checked) return;
                const idx = this.rows.findIndex(r => r.nameInput === nameInput);
                if (idx === -1) return;
                if (cbField.checked) {
                    this.lastFieldIdx = (idx - 1 + this.rows.length) % this.rows.length;
                    this.markNextPlayers();
                } else if (cbBench.checked) {
                    this.lastBenchIdx = (idx - 1 + this.rows.length) % this.rows.length;
                    this.markNextPlayers();
                }
            };
            nameInput.addEventListener('click', handleSelectAsNext);
            nameInput.addEventListener('touchend', handleSelectAsNext, { passive: true });

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
                            this.rows.splice(draggedIndex, 1);
                            const newTargetIndex = draggedIndex < targetIndex ? targetIndex : targetIndex;
                            this.rows.splice(newTargetIndex, 0, this.draggedRow);

                            // Rebuild DOM with proper drag/drop
                            this.rebuildDOM();

                            this.lastFieldIdx = -1;
                            this.lastBenchIdx = -1;
                            
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
        
        // Update Min View visibility after rotation
        if (this.viewMode === 2) {
            this.updateMinViewVisibility();
            this.updateDynamicSizing();
        }
        
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
                players: this.rows.map(r => ({
                    name: r.nameInput.value,
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
            if (typeof model.viewMode === 'number' && model.viewMode >= 0 && model.viewMode <= 2) {
                this.viewMode = model.viewMode;
                // Apply view mode classes
                if (this.viewMode === 1) {
                    document.body.classList.add('less-view');
                    this.viewlessBtn.textContent = 'zoom';
                } else if (this.viewMode === 2) {
                    document.body.classList.add('less-view', 'min-view');
                    this.viewlessBtn.textContent = 'MIN';
                }
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
            
            // Apply min view visibility if in min mode
            if (this.viewMode === 2) {
                this.updateMinViewVisibility();
            }
        } catch (error) {
            console.error('[RosterManager] Error loading from storage:', error);
            localStorage.removeItem(this.STORAGE_KEY);
        }
    }

    markNextPlayers() {
        this.rows.forEach(r => r.tr.classList.remove('player-next'));

        const benchCandidates = [];
        const fieldCandidates = this.rows.filter(r => !r.cbInactive.checked && (r.cbField.checked || r.cbGoalie.checked));
        this.rows.forEach((r, idx) => {
            if (r.cbBench.checked && !r.cbInactive.checked && !r.cbGoalie.checked) benchCandidates.push(idx);
            if (r.cbField.checked && !r.cbInactive.checked && !r.cbGoalie.checked) fieldCandidates.push(idx);
        });

        const nextFieldIdx = this._nextIndexFrom(fieldCandidates, this.lastFieldIdx);
        const nextBenchIdx = this._nextIndexFrom(benchCandidates, this.lastBenchIdx);

        if (nextFieldIdx !== -1) this.rows[nextFieldIdx].tr.classList.add('player-next');
        if (nextBenchIdx !== -1) this.rows[nextBenchIdx].tr.classList.add('player-next');
    }

    _nextIndexFrom(candidates, lastIdx) {
        if (candidates.length === 0) return -1;
        for (let i = 1; i <= this.rows.length; i++) {
            const probe = (lastIdx + i) % this.rows.length;
            if (candidates.includes(probe)) return probe;
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
}

// Initialize when DOM is ready
window.addEventListener('DOMContentLoaded', () => {
    new RosterManager();
});
