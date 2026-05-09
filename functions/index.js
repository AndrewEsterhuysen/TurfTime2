const { onDocumentCreated } = require('firebase-functions/v2/firestore');
const { onCall, HttpsError } = require('firebase-functions/v2/https');
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

/**
 * Callable function: requestAdminCodeEmail
 * 
 * Called by a team creator to trigger an email with their admin recovery code.
 * The plain-text admin code is NEVER stored in Firestore — only its SHA-256 hash is.
 * This function therefore cannot email the code directly; instead it emails the
 * team metadata (Team ID + Team Name) so the creator knows which team they own,
 * and reminds them that the admin code was shown only once at creation time.
 * 
 * If you integrate a transactional email provider (e.g. SendGrid via Firebase Extensions
 * or a custom SMTP relay) you can extend this function to deliver the reminder email.
 * 
 * Request payload: { teamId: string }
 * Returns: { status: "sent" | "not_found" | "error", teamName?: string }
 */
exports.requestAdminCodeEmail = onCall(async (request) => {
    const teamId = request.data?.teamId;

    if (!teamId || typeof teamId !== 'string') {
        throw new HttpsError('invalid-argument', 'teamId is required.');
    }

    try {
        const db = getFirestore();
        const metadataSnap = await db
            .collection('teams')
            .doc(teamId)
            .collection('metadata')
            .doc('info')
            .get();

        if (!metadataSnap.exists) {
            return { status: 'not_found' };
        }

        const metadata = metadataSnap.data();
        const teamName = metadata.teamName || teamId;
        const creatorEmail = metadata.creatorEmail || null;
        const createdBy = metadata.createdBy || null;

        console.log(`[requestAdminCodeEmail] Recovery reminder requested for team ${teamId} (${teamName}) by uid ${createdBy}`);

        if (!creatorEmail) {
            // No email registered — return success anyway so app doesn't alarm the user
            console.log(`[requestAdminCodeEmail] No creatorEmail stored for team ${teamId} — skipping email`);
            return { status: 'sent', teamName };
        }

        // Write to the 'mail' collection — requires the Firebase "Trigger Email" extension
        // https://extensions.dev/extensions/firebase/firestore-send-email
        await db.collection('mail').add({
            to: creatorEmail,
            message: {
                subject: `TurfTimer – Admin Recovery Reminder for ${teamName}`,
                text:
                    `Hi,\n\n` +
                    `You requested an Admin Recovery Code reminder for your TurfTimer team.\n\n` +
                    `Team Name: ${teamName}\n` +
                    `Team ID:   ${teamId}\n\n` +
                    `Your Admin Recovery Code was shown once when you created the team.\n` +
                    `For security reasons it is not stored and cannot be retrieved.\n\n` +
                    `If you have permanently lost the code, you will need to create a new team.\n\n` +
                    `– TurfTimer`,
                html:
                    `<p>Hi,</p>` +
                    `<p>You requested an Admin Recovery Code reminder for your TurfTimer team.</p>` +
                    `<table><tr><td><b>Team Name:</b></td><td>${teamName}</td></tr>` +
                    `<tr><td><b>Team ID:</b></td><td><code>${teamId}</code></td></tr></table>` +
                    `<p>Your Admin Recovery Code was shown once when you created the team.<br>` +
                    `For security reasons it is <b>not stored</b> and cannot be retrieved.</p>` +
                    `<p>If you have permanently lost the code, you will need to create a new team.</p>` +
                    `<p>– TurfTimer</p>`
            }
        });

        console.log(`[requestAdminCodeEmail] Mail document created for ${creatorEmail}`);
        return { status: 'sent', teamName };

    } catch (error) {
        console.error('[requestAdminCodeEmail] Error:', error);
        throw new HttpsError('internal', 'Could not process request.');
    }
});
