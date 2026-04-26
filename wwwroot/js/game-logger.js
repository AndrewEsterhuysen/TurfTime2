// Game Event Logger - Tracks all player and timer changes during a soccer match

class GameLogger {
    constructor() {
        this.STORAGE_KEYS = {
            CURRENT_SESSION: 'roster.currentSession.v1',
            SESSION_HISTORY: 'roster.sessionHistory.v1'
        };
        
        this.EVENT_TYPES = {
            // Player position changes
            PLAYER_TO_FIELD: 'player_to_field',
            PLAYER_TO_BENCH: 'player_to_bench',
            PLAYER_TO_GOALIE: 'player_to_goalie',
            PLAYER_TO_INACTIVE: 'player_to_inactive',
            
            // Timer events
            MATCH_TIMER_CHANGED: 'match_timer_changed',
            ROTATION_TIMER_CHANGED: 'rotation_timer_changed',
            
            // Game state changes
            GAME_STARTED: 'game_started',
            GAME_PAUSED: 'game_paused',
            GAME_RESUMED: 'game_resumed',
            GAME_RESTARTED: 'game_restarted',
            HALF_TIME: 'half_time',
            SECOND_HALF_STARTED: 'second_half_started',
            GAME_ENDED: 'game_ended',
            
            // Rotation events
            ROTATION_EXECUTED: 'rotation_executed',
            MANUAL_NEXT_SELECTION: 'manual_next_selection',
            PLAYER_REORDERED: 'player_reordered'
        };
        
        this.currentSession = null;
        this.rosterManager = null; // Will be set by RosterManager
        
        this.loadCurrentSession();
    }
    
    // Set reference to RosterManager for accessing game state
    setRosterManager(manager) {
        this.rosterManager = manager;
    }
    
    // Initialize a new game session
    startSession(matchDuration, rotationInterval, location = null) {
        console.log('[GameLogger] ⭐ startSession() called');
        console.log('[GameLogger] ⭐ Match Duration:', matchDuration, 'seconds');
        console.log('[GameLogger] ⭐ Rotation Interval:', rotationInterval, 'seconds');
        console.log('[GameLogger] ⭐ Location:', location);

        // Archive previous session if exists
        if (this.currentSession && !this.currentSession.endTime) {
            console.log('[GameLogger] ⚠️ Previous session exists, ending it first');
            this.endSession();
        }

        this.currentSession = {
            sessionId: this.generateUUID(),
            startTime: new Date().toISOString(),
            endTime: null,
            location: location,
            matchDuration: matchDuration,
            rotationInterval: rotationInterval,
            logs: [],
            summary: null
        };

        console.log('[GameLogger] ✅ New session created:', this.currentSession.sessionId);
        console.log('[GameLogger] 📊 Session object:', this.currentSession);

        this.log(
            this.EVENT_TYPES.GAME_STARTED,
            `Match started - ${Math.floor(matchDuration / 60)} minute game`,
            null,
            { matchDuration, rotationInterval }
        );

        this.saveSession();
        console.log('[GameLogger] ✅ Session saved to localStorage');
    }
    
    // Log an event
    log(eventType, description, playerName = null, details = {}) {
        if (!this.currentSession) {
            console.warn('[GameLogger] No active session, cannot log event');
            return;
        }
        
        const entry = {
            id: this.generateUUID(),
            timestamp: new Date().toISOString(),
            eventType: eventType,
            description: description,
            playerName: playerName,
            details: {
                ...details,
                gameState: this.captureGameState()
            }
        };
        
        this.currentSession.logs.push(entry);
        this.saveSession();
        console.log('[GameLogger]', eventType, ':', description);
    }
    
    // Capture current game state
    captureGameState() {
        if (!this.rosterManager) {
            return null;
        }
        
        try {
            return {
                currentHalf: this.rosterManager.currentHalf,
                matchTimeRemaining: this.rosterManager.matchRemainingSeconds,
                rotationTimeRemaining: this.rosterManager.countdownRemaining,
                fieldPlayerCount: this.countPlayers('field'),
                benchPlayerCount: this.countPlayers('bench'),
                goaliePlayerCount: this.countPlayers('goalie')
            };
        } catch (error) {
            console.error('[GameLogger] Error capturing game state:', error);
            return null;
        }
    }
    
    // Count players in specific position
    countPlayers(position) {
        if (!this.rosterManager || !this.rosterManager.rows) {
            return 0;
        }
        
        switch (position) {
            case 'field':
                return this.rosterManager.rows.filter(r => 
                    r.cbField.checked && !r.cbInactive.checked && !r.cbGoalie.checked
                ).length;
            case 'bench':
                return this.rosterManager.rows.filter(r => 
                    r.cbBench.checked && !r.cbInactive.checked
                ).length;
            case 'goalie':
                return this.rosterManager.rows.filter(r => 
                    r.cbGoalie.checked && !r.cbInactive.checked
                ).length;
            case 'inactive':
                return this.rosterManager.rows.filter(r => 
                    r.cbInactive.checked
                ).length;
            default:
                return 0;
        }
    }
    
    // End current session and calculate summary
    endSession() {
        console.log('[GameLogger] ⚠️ endSession() called');

        if (!this.currentSession) {
            console.warn('[GameLogger] ❌ No current session to end!');
            return;
        }

        console.log('[GameLogger] 📊 Current session ID:', this.currentSession.sessionId);
        console.log('[GameLogger] 📊 Session logs count:', this.currentSession.logs?.length || 0);

        this.currentSession.endTime = new Date().toISOString();
        this.currentSession.summary = this.calculateSummary();

        console.log('[GameLogger] 📊 Summary calculated:', this.currentSession.summary);

        this.log(
            this.EVENT_TYPES.GAME_ENDED,
            'Match ended',
            null,
            { duration: this.currentSession.summary.duration }
        );

        console.log('[GameLogger] 🔄 Calling archiveSession()...');
        this.archiveSession();
        console.log('[GameLogger] ✅ Session ended and archived:', this.currentSession.sessionId);
    }
    
    // Calculate session statistics
    calculateSummary() {
        const summary = {
            totalRotations: 0,
            playerStats: [],
            duration: 0
        };
        
        // Calculate total rotations
        summary.totalRotations = this.currentSession.logs.filter(
            log => log.eventType === this.EVENT_TYPES.ROTATION_EXECUTED
        ).length;
        
        // Calculate game duration
        if (this.currentSession.endTime) {
            const start = new Date(this.currentSession.startTime);
            const end = new Date(this.currentSession.endTime);
            summary.duration = Math.floor((end - start) / 1000); // in seconds
        }
        
        // Calculate player statistics
        summary.playerStats = this.aggregatePlayerStats();
        
        return summary;
    }
    
    // Aggregate player statistics from logs
    aggregatePlayerStats() {
        const playerMap = new Map();

        // Process all logs
        this.currentSession.logs.forEach(log => {
            // Handle rotation logs specially - they don't have playerName at the top level
            if (log.eventType === this.EVENT_TYPES.ROTATION_EXECUTED && log.details) {
                const playerIn = log.details.playerIn;
                const playerOut = log.details.playerOut;

                // Count rotation for player coming IN
                if (playerIn) {
                    if (!playerMap.has(playerIn)) {
                        playerMap.set(playerIn, {
                            playerName: playerIn,
                            timeOnField: 0,
                            timeOnBench: 0,
                            timeAsGoalie: 0,
                            rotationsIn: 0,
                            rotationsOut: 0
                        });
                    }
                    playerMap.get(playerIn).rotationsIn++;
                }

                // Count rotation for player going OUT
                if (playerOut) {
                    if (!playerMap.has(playerOut)) {
                        playerMap.set(playerOut, {
                            playerName: playerOut,
                            timeOnField: 0,
                            timeOnBench: 0,
                            timeAsGoalie: 0,
                            rotationsIn: 0,
                            rotationsOut: 0
                        });
                    }
                    playerMap.get(playerOut).rotationsOut++;
                }
            }

            // Handle other logs with playerName (if we add them later)
            if (log.playerName) {
                if (!playerMap.has(log.playerName)) {
                    playerMap.set(log.playerName, {
                        playerName: log.playerName,
                        timeOnField: 0,
                        timeOnBench: 0,
                        timeAsGoalie: 0,
                        rotationsIn: 0,
                        rotationsOut: 0
                    });
                }
            }
        });

        // Get final counter values and calculate bench time from RosterManager
        if (this.rosterManager && this.rosterManager.rows) {
            // Get total game duration in seconds
            const gameDuration = this.currentSession.matchDuration || 0;

            this.rosterManager.rows.forEach(row => {
                const name = row.nameInput.value;
                if (!name) return; // Skip empty player names

                // Create stats entry if player wasn't in any rotation logs
                if (!playerMap.has(name)) {
                    playerMap.set(name, {
                        playerName: name,
                        timeOnField: 0,
                        timeOnBench: 0,
                        timeAsGoalie: 0,
                        rotationsIn: 0,
                        rotationsOut: 0
                    });
                }

                const stats = playerMap.get(name);

                // Field time is tracked by the counter
                stats.timeOnField = row.counterSeconds || 0;

                // Calculate bench time
                // Bench time = Game Duration - Field Time (assuming player was either on field or bench)
                // If player is inactive, they have 0 bench time
                const isInactive = row.cbInactive && row.cbInactive.checked;
                if (!isInactive && gameDuration > 0) {
                    stats.timeOnBench = Math.max(0, gameDuration - stats.timeOnField);
                }

                // Track goalie time (if applicable)
                if (row.cbGoalie && row.cbGoalie.checked) {
                    stats.timeAsGoalie = stats.timeOnField;
                }
            });
        }

        return Array.from(playerMap.values());
    }
    
    // Archive current session to history
    archiveSession() {
        console.log('[GameLogger] 🗄️ archiveSession() called');

        try {
            if (!this.currentSession) {
                console.error('[GameLogger] ❌ No current session to archive!');
                return;
            }

            console.log('[GameLogger] 📦 Archiving session:', this.currentSession.sessionId);
            console.log('[GameLogger] 📦 Session data:', JSON.stringify(this.currentSession, null, 2).substring(0, 500) + '...');

            const history = this.loadSessionHistory();
            history.sessions.unshift(this.currentSession);

            // Keep only last 20 sessions
            if (history.sessions.length > 20) {
                history.sessions = history.sessions.slice(0, 20);
            }

            localStorage.setItem(
                this.STORAGE_KEYS.SESSION_HISTORY,
                JSON.stringify(history)
            );
            console.log('[GameLogger] ✅ Saved to localStorage history');

            // Save session to Firestore for cloud access
            console.log('[GameLogger] ☁️ Attempting Firestore save...');
            this.saveSessionToFirestore(this.currentSession);

            // Clear current session
            localStorage.removeItem(this.STORAGE_KEYS.CURRENT_SESSION);
            this.currentSession = null;

            console.log('[GameLogger] ✅ Session archived to history and Firestore');
        } catch (error) {
            console.error('[GameLogger] ❌ Error archiving session:', error);
            console.error('[GameLogger] ❌ Stack trace:', error.stack);
        }
    }

    // Save session to Firestore
    async saveSessionToFirestore(session) {
        console.log('[GameLogger] 🔵 saveSessionToFirestore() called');

        try {
            // Get team ID from localStorage (stored by C# as 'team_id')
            const teamId = localStorage.getItem('team_id');
            console.log('[GameLogger] 🔵 Team ID from localStorage:', teamId);

            if (!teamId) {
                console.warn('[GameLogger] ⚠️ No team ID available, skipping Firestore save');
                console.warn('[GameLogger] 🔍 Available localStorage keys:', Object.keys(localStorage).join(', '));
                return;
            }

            // Check if this is a local team (local teams don't need cloud save)
            if (teamId.startsWith('local_')) {
                console.log('[GameLogger] ℹ️ Local team detected, skipping Firestore save (localStorage only)');
                return;
            }

            // Call C# bridge to save session to Firestore (cloud teams only)
            if (window.csharpSaveSession) {
                const sessionJson = JSON.stringify(session);
                console.log('[GameLogger] ✅ C# bridge found, preparing to save to Firestore');
                console.log('[GameLogger] 📤 Team ID:', teamId);
                console.log('[GameLogger] 📤 Session ID:', session.sessionId);
                console.log('[GameLogger] 📤 Session data length:', sessionJson.length);
                console.log('[GameLogger] 📤 Calling window.csharpSaveSession.postMessage()...');

                window.csharpSaveSession.postMessage(JSON.stringify({
                    teamId: teamId,
                    sessionData: sessionJson
                }));

                console.log('[GameLogger] ✅ Message posted to C# bridge for Firestore save');
            } else {
                console.error('[GameLogger] ❌ C# session save bridge NOT available!');
                console.error('[GameLogger] 🔍 window.csharpSaveSession:', window.csharpSaveSession);
                console.error('[GameLogger] 🔍 window.csharpSaveRoster:', window.csharpSaveRoster ? 'EXISTS' : 'MISSING');
            }
        } catch (error) {
            console.error('[GameLogger] ❌ Error saving session to Firestore:', error);
            console.error('[GameLogger] ❌ Stack trace:', error.stack);
        }
    }

    // Save current session to localStorage
    saveSession() {
        if (!this.currentSession) return;
        
        try {
            localStorage.setItem(
                this.STORAGE_KEYS.CURRENT_SESSION,
                JSON.stringify({
                    session: this.currentSession,
                    isActive: !this.currentSession.endTime
                })
            );
        } catch (error) {
            console.error('[GameLogger] Error saving session:', error);
        }
    }
    
    // Load current session from localStorage
    loadCurrentSession() {
        try {
            const raw = localStorage.getItem(this.STORAGE_KEYS.CURRENT_SESSION);
            if (!raw) return;
            
            const data = JSON.parse(raw);
            if (data.session && data.isActive) {
                this.currentSession = data.session;
                console.log('[GameLogger] Loaded active session:', this.currentSession.sessionId);
            }
        } catch (error) {
            console.error('[GameLogger] Error loading session:', error);
        }
    }
    
    // Load session history from localStorage
    loadSessionHistory() {
        try {
            const raw = localStorage.getItem(this.STORAGE_KEYS.SESSION_HISTORY);
            if (!raw) {
                return { sessions: [] };
            }
            return JSON.parse(raw);
        } catch (error) {
            console.error('[GameLogger] Error loading history:', error);
            return { sessions: [] };
        }
    }
    
    // Get all historical sessions
    getSessionHistory() {
        return this.loadSessionHistory().sessions;
    }
    
    // Get specific session by ID
    getSession(sessionId) {
        if (this.currentSession && this.currentSession.sessionId === sessionId) {
            return this.currentSession;
        }
        
        const history = this.loadSessionHistory();
        return history.sessions.find(s => s.sessionId === sessionId);
    }
    
    // Clear all session history
    clearHistory() {
        try {
            localStorage.removeItem(this.STORAGE_KEYS.SESSION_HISTORY);
            console.log('[GameLogger] Session history cleared');
        } catch (error) {
            console.error('[GameLogger] Error clearing history:', error);
        }
    }
    
    // Generate UUID (simple version)
    generateUUID() {
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
            const r = Math.random() * 16 | 0;
            const v = c === 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }
    
    // Export session as CSV
    exportAsCSV(session) {
        const csv = [
            ['Timestamp', 'Event Type', 'Description', 'Player', 'Half', 'Match Time', 'Rotation Time']
        ];
        
        session.logs.forEach(log => {
            const gameState = log.details.gameState || {};
            csv.push([
                log.timestamp,
                log.eventType,
                log.description,
                log.playerName || '',
                gameState.currentHalf || '',
                gameState.matchTimeRemaining || '',
                gameState.rotationTimeRemaining || ''
            ]);
        });
        
        return csv.map(row => row.map(cell => `"${cell}"`).join(',')).join('\n');
    }
    
    // Export session as JSON
    exportAsJSON(session) {
        return JSON.stringify(session, null, 2);
    }
    
    // Generate summary report
    generateSummaryReport(session) {
        const start = new Date(session.startTime);
        const duration = session.summary ? session.summary.duration : 0;
        const hours = Math.floor(duration / 3600);
        const minutes = Math.floor((duration % 3600) / 60);
        const seconds = duration % 60;
        
        let report = `
???????????????????????????????????????
         GAME SESSION REPORT
???????????????????????????????????????

Date: ${start.toLocaleDateString()}
Time: ${start.toLocaleTimeString()}
${session.location ? `Location: ${session.location}\n` : ''}
Duration: ${hours}h ${minutes}m ${seconds}s
Total Rotations: ${session.summary?.totalRotations || 0}

???????????????????????????????????????
           PLAYER STATISTICS
???????????????????????????????????????
`;
        
        if (session.summary && session.summary.playerStats.length > 0) {
            session.summary.playerStats
                .filter(p => p.timeOnField > 0)
                .sort((a, b) => b.timeOnField - a.timeOnField)
                .forEach(player => {
                    const mins = Math.floor(player.timeOnField / 60);
                    const secs = player.timeOnField % 60;
                    report += `\n${player.playerName}:
  Field Time: ${mins}m ${secs}s
  Rotations In: ${player.rotationsIn}
  Rotations Out: ${player.rotationsOut}\n`;
                });
        } else {
            report += '\nNo player statistics available.\n';
        }
        
        report += '\n???????????????????????????????????????\n';
        
        return report;
    }
}

// Make GameLogger available globally
window.GameLogger = GameLogger;
