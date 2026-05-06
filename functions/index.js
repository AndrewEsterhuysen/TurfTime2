const { onDocumentCreated } = require('firebase-functions/v2/firestore');
const { initializeApp } = require('firebase-admin/app');
const { getFirestore } = require('firebase-admin/firestore');
const { getMessaging } = require('firebase-admin/messaging');

initializeApp();

// Trigger: New message added to messages collection (2nd Gen)
exports.sendChatNotification = onDocumentCreated('teams/{teamId}/messages/{messageId}', async (event) => {
    try {
        const snapshot = event.data;
        if (!snapshot) {
            console.log('No data associated with the event');
            return;
        }

        const message = snapshot.data();
        const teamId = event.params.teamId;

        console.log(`New message in team ${teamId} from ${message.senderName || 'Unknown'}`);

        // Get team info
        const teamDoc = await getFirestore()
            .collection('teams')
            .doc(teamId)
            .get();

        if (!teamDoc.exists) {
            console.log('Team not found');
            return null;
        }

        const teamData = teamDoc.data();
        const teamName = teamData.name || 'Your Team';

        // Get all team members' FCM tokens (except sender)
        const membersSnapshot = await getFirestore()
            .collection('teams')
            .doc(teamId)
            .collection('members')
            .get();

        const tokens = [];
        const tokenToMemberMap = {}; // Track which member each token belongs to

        membersSnapshot.forEach(doc => {
            const member = doc.data();
            const memberId = doc.id;

            // Don't notify the sender
            if (memberId === message.userId || member.uid === message.userId) {
                return;
            }

            // Support both fcmTokens (array) and fcmToken (single string) for backward compatibility
            if (member.fcmTokens && Array.isArray(member.fcmTokens)) {
                // New multi-device format
                member.fcmTokens.forEach(token => {
                    if (token) {
                        tokens.push(token);
                        tokenToMemberMap[token] = memberId;
                    }
                });
            } else if (member.fcmToken) {
                // Old single-device format (backward compatibility)
                tokens.push(member.fcmToken);
                tokenToMemberMap[member.fcmToken] = memberId;
            }
        });

        if (tokens.length === 0) {
            console.log('No tokens to send to');
            return null;
        }

        console.log(`Sending notification to ${tokens.length} device(s)`);

        // Create notification payload
        const payload = {
            notification: {
                title: `💬 ${teamName}`,
                body: `${message.senderName || message.userId?.substring(0, 8) || 'Someone'}: ${message.text || 'New message'}`
            },
            data: {
                teamId: teamId,
                messageId: event.params.messageId,
                type: 'chat_message'
            }
        };

        // Send notification to all tokens
        const response = await getMessaging().sendEachForMulticast({
            tokens: tokens,
            notification: payload.notification,
            data: payload.data,
            android: {
                notification: {
                    icon: 'notification_icon',
                    sound: 'default'
                }
            }
        });

        console.log(`Notification sent. Success: ${response.successCount}, Failure: ${response.failureCount}`);

        // Clean up invalid tokens
        const failedTokens = [];
        response.responses.forEach((resp, idx) => {
            if (!resp.success) {
                console.error('Failure sending to', tokens[idx], resp.error);
                // Remove invalid tokens
                if (resp.error?.code === 'messaging/invalid-registration-token' ||
                    resp.error?.code === 'messaging/registration-token-not-registered') {
                    failedTokens.push(tokens[idx]);
                }
            }
        });

        // Remove invalid tokens from Firestore
        if (failedTokens.length > 0) {
            console.log(`Cleaning up ${failedTokens.length} invalid token(s)`);

            const batch = getFirestore().batch();

            // Group failed tokens by member
            const memberTokensToRemove = {};
            failedTokens.forEach(token => {
                const memberId = tokenToMemberMap[token];
                if (memberId) {
                    if (!memberTokensToRemove[memberId]) {
                        memberTokensToRemove[memberId] = [];
                    }
                    memberTokensToRemove[memberId].push(token);
                }
            });

            // Update each member document
            for (const [memberId, tokensToRemove] of Object.entries(memberTokensToRemove)) {
                const memberRef = getFirestore()
                    .collection('teams')
                    .doc(teamId)
                    .collection('members')
                    .doc(memberId);

                const memberDoc = await memberRef.get();
                const memberData = memberDoc.data();

                if (memberData.fcmTokens && Array.isArray(memberData.fcmTokens)) {
                    // Remove invalid tokens from array
                    const updatedTokens = memberData.fcmTokens.filter(t => !tokensToRemove.includes(t));
                    batch.update(memberRef, { fcmTokens: updatedTokens });
                } else if (memberData.fcmToken && tokensToRemove.includes(memberData.fcmToken)) {
                    // Old format - remove the single token
                    batch.update(memberRef, { fcmToken: getFirestore().FieldValue.delete() });
                }
            }

            await batch.commit();
            console.log(`✅ Cleaned up invalid tokens from ${Object.keys(memberTokensToRemove).length} member(s)`);
        }

        return null;
    } catch (error) {
        console.error('Error sending notification:', error);
        return null;
    }
});

// Log when function is ready
console.log('💬 Chat notification function loaded successfully (2nd Gen)');
