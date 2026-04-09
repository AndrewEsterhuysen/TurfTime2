// Team Service - Firebase Firestore team management
// Handles team creation, joining, and data sync

class TeamService {
    constructor() {
        this.db = null;
        this.auth = null;
        this.currentTeamId = null;
        this.currentUserId = null;
        this.onTeamChangeCallback = null;
    }

    // Initialize with Firebase instances
    initialize(db, auth) {
        this.db = db;
        this.auth = auth;
        
        // Listen for auth state changes
        this.auth.onAuthStateChanged((user) => {
            if (user) {
                this.currentUserId = user.uid;
                console.log('[TeamService] User authenticated:', user.uid.substring(0, 8));
            } else {
                this.currentUserId = null;
                console.log('[TeamService] User signed out');
            }
        });
    }

    // Set current team ID
    setCurrentTeam(teamId) {
        this.currentTeamId = teamId;
        localStorage.setItem('current_team_id', teamId);
        console.log('[TeamService] Current team set:', teamId);
        
        if (this.onTeamChangeCallback) {
            this.onTeamChangeCallback(teamId);
        }
    }

    // Get current team ID
    getCurrentTeam() {
        if (!this.currentTeamId) {
            this.currentTeamId = localStorage.getItem('current_team_id');
        }
        return this.currentTeamId;
    }

    // Create a new team
    async createTeam(teamId, teamName, inviteCode) {
        if (!this.currentUserId) {
            throw new Error('User not authenticated');
        }

        try {
            console.log('[TeamService] Creating team:', teamId);

            // Create team metadata
            await this.db.collection('teams').doc(teamId).collection('metadata').doc('info').set({
                teamName: teamName,
                inviteCode: inviteCode,
                createdBy: this.currentUserId,
                createdAt: firebase.firestore.FieldValue.serverTimestamp(),
                isActive: true
            });

            // Add creator as admin member
            await this.db.collection('teams').doc(teamId).collection('members').doc(this.currentUserId).set({
                role: 'admin',
                joinedAt: firebase.firestore.FieldValue.serverTimestamp(),
                displayName: 'Admin'
            });

            // Initialize empty roster
            await this.db.collection('teams').doc(teamId).collection('roster').doc('data').set({
                version: 2,
                lastModified: firebase.firestore.FieldValue.serverTimestamp(),
                players: []
            });

            console.log('[TeamService] ✅ Team created successfully');
            return { success: true, teamId: teamId };
        } catch (error) {
            console.error('[TeamService] ❌ Create team error:', error);
            throw error;
        }
    }

    // Join an existing team using invite code
    async joinTeam(inviteCode) {
        if (!this.currentUserId) {
            throw new Error('User not authenticated');
        }

        try {
            console.log('[TeamService] Searching for team with invite code:', inviteCode);

            // Search for team with matching invite code
            const teamsSnapshot = await this.db.collectionGroup('metadata')
                .where('inviteCode', '==', inviteCode.toUpperCase())
                .where('isActive', '==', true)
                .limit(1)
                .get();

            if (teamsSnapshot.empty) {
                throw new Error('Invalid invite code. Team not found.');
            }

            const metadataDoc = teamsSnapshot.docs[0];
            const teamId = metadataDoc.ref.parent.parent.id;
            const teamData = metadataDoc.data();

            console.log('[TeamService] Found team:', teamId);

            // Check if already a member
            const memberDoc = await this.db.collection('teams').doc(teamId)
                .collection('members').doc(this.currentUserId).get();

            if (memberDoc.exists) {
                console.log('[TeamService] Already a member of this team');
                return { 
                    success: true, 
                    teamId: teamId, 
                    teamName: teamData.teamName,
                    alreadyMember: true
                };
            }

            // Add as member
            await this.db.collection('teams').doc(teamId)
                .collection('members').doc(this.currentUserId).set({
                    role: 'member',
                    joinedAt: firebase.firestore.FieldValue.serverTimestamp(),
                    displayName: 'Member'
                });

            console.log('[TeamService] ✅ Joined team successfully');
            return { 
                success: true, 
                teamId: teamId, 
                teamName: teamData.teamName,
                alreadyMember: false
            };
        } catch (error) {
            console.error('[TeamService] ❌ Join team error:', error);
            throw error;
        }
    }

    // Get team metadata
    async getTeamMetadata(teamId) {
        try {
            const metadataDoc = await this.db.collection('teams').doc(teamId)
                .collection('metadata').doc('info').get();
            
            if (metadataDoc.exists) {
                return metadataDoc.data();
            }
            return null;
        } catch (error) {
            console.error('[TeamService] Get metadata error:', error);
            throw error;
        }
    }

    // Get team members
    async getTeamMembers(teamId) {
        try {
            const membersSnapshot = await this.db.collection('teams').doc(teamId)
                .collection('members').get();
            
            const members = [];
            membersSnapshot.forEach(doc => {
                members.push({
                    userId: doc.id,
                    ...doc.data()
                });
            });
            return members;
        } catch (error) {
            console.error('[TeamService] Get members error:', error);
            throw error;
        }
    }

    // Get user's role in team
    async getUserRole(teamId, userId = null) {
        const uid = userId || this.currentUserId;
        if (!uid) return null;

        try {
            const memberDoc = await this.db.collection('teams').doc(teamId)
                .collection('members').doc(uid).get();
            
            if (memberDoc.exists) {
                return memberDoc.data().role;
            }
            return null;
        } catch (error) {
            console.error('[TeamService] Get user role error:', error);
            return null;
        }
    }

    // Regenerate invite code
    async regenerateInviteCode(teamId, newInviteCode) {
        try {
            await this.db.collection('teams').doc(teamId)
                .collection('metadata').doc('info').update({
                    inviteCode: newInviteCode,
                    inviteCodeUpdatedAt: firebase.firestore.FieldValue.serverTimestamp()
                });
            
            console.log('[TeamService] ✅ Invite code regenerated');
            return { success: true };
        } catch (error) {
            console.error('[TeamService] ❌ Regenerate code error:', error);
            throw error;
        }
    }

    // Leave team (remove self from members)
    async leaveTeam(teamId) {
        if (!this.currentUserId) {
            throw new Error('User not authenticated');
        }

        try {
            // Check if user is the last admin
            const role = await this.getUserRole(teamId);
            if (role === 'admin') {
                const members = await this.getTeamMembers(teamId);
                const admins = members.filter(m => m.role === 'admin');
                
                if (admins.length === 1) {
                    throw new Error('Cannot leave team. You are the last admin. Please promote another member first.');
                }
            }

            // Remove from members
            await this.db.collection('teams').doc(teamId)
                .collection('members').doc(this.currentUserId).delete();
            
            console.log('[TeamService] ✅ Left team successfully');
            return { success: true };
        } catch (error) {
            console.error('[TeamService] ❌ Leave team error:', error);
            throw error;
        }
    }

    // Promote member to admin
    async promoteMember(teamId, userId) {
        try {
            await this.db.collection('teams').doc(teamId)
                .collection('members').doc(userId).update({
                    role: 'admin',
                    promotedAt: firebase.firestore.FieldValue.serverTimestamp()
                });
            
            console.log('[TeamService] ✅ Member promoted to admin');
            return { success: true };
        } catch (error) {
            console.error('[TeamService] ❌ Promote member error:', error);
            throw error;
        }
    }

    // Remove member from team (admin only)
    async removeMember(teamId, userId) {
        try {
            await this.db.collection('teams').doc(teamId)
                .collection('members').doc(userId).delete();
            
            console.log('[TeamService] ✅ Member removed from team');
            return { success: true };
        } catch (error) {
            console.error('[TeamService] ❌ Remove member error:', error);
            throw error;
        }
    }

    // Listen to team changes (for real-time updates)
    onTeamChange(callback) {
        this.onTeamChangeCallback = callback;
    }
}

// Export singleton instance
window.teamService = new TeamService();
console.log('[TeamService] Initialized');
