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
        // Archive previous session if exists
        if (this.currentSession && !this.currentSession.endTime) {
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
        
        this.log(
            this.EVENT_TYPES.GAME_STARTED,
            `Match started - ${Math.floor(matchDuration / 60)} minute game`,
            null,
            { matchDuration, rotationInterval }
        );
        
        this.saveSession();
        console.log('[GameLogger] New session started:', this.currentSession.sessionId);
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
        if (!this.currentSession) {
            return;
        }
        
        this.currentSession.endTime = new Date().toISOString();
        this.currentSession.summary = this.calculateSummary();
        
        this.log(
            this.EVENT_TYPES.GAME_ENDED,
            'Match ended',
            null,
            { duration: this.currentSession.summary.duration }
        );
        
        this.archiveSession();
        console.log('[GameLogger] Session ended:', this.currentSession.sessionId);
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
        
        // Process all player position change logs
        this.currentSession.logs.forEach(log => {
            if (!log.playerName) return;
            
            if (!playerMap.has(log.playerName)) {
                playerMap.set(log.playerName, {
                    playerName: log.playerName,
                    timeOnField: 0,
                    timeAsBench: 0,
                    timeAsGoalie: 0,
                    rotationsIn: 0,
                    rotationsOut: 0
                });
            }
            
            const stats = playerMap.get(log.playerName);
            
            // Count rotations
            if (log.eventType === this.EVENT_TYPES.ROTATION_EXECUTED) {
                if (log.details.playerIn === log.playerName) {
                    stats.rotationsIn++;
                }
                if (log.details.playerOut === log.playerName) {
                    stats.rotationsOut++;
                }
            }
        });
        
        // Get final counter values from RosterManager
        if (this.rosterManager && this.rosterManager.rows) {
            this.rosterManager.rows.forEach(row => {
                const name = row.nameInput.value;
                if (playerMap.has(name)) {
                    const stats = playerMap.get(name);
                    stats.timeOnField = row.counterSeconds;
                }
            });
        }
        
        return Array.from(playerMap.values());
    }
    
    // Archive current session to history
    archiveSession() {
        try {
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
            
            // Clear current session
            localStorage.removeItem(this.STORAGE_KEYS.CURRENT_SESSION);
            this.currentSession = null;
            
            console.log('[GameLogger] Session archived to history');
        } catch (error) {
            console.error('[GameLogger] Error archiving session:', error);
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
