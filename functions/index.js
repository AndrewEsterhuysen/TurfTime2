const { onDocumentCreated } = require('firebase-functions/v2/firestore');
const { onCall, HttpsError } = require('firebase-functions/v2/https');
const { initializeApp } = require('firebase-admin/app');
const { getFirestore, FieldValue } = require('firebase-admin/firestore');
const { getMessaging } = require('firebase-admin/messaging');

initializeApp();

/**
 * Resolve a human-readable team name for notification titles.
 * App stores metadata at teams/{teamId}/metadata/info.teamName — there is often
 * no root teams/{teamId} document. Never fail notification send on missing name.
 */
async function resolveTeamName(db, teamId) {
    try {
        const metaSnap = await db
            .collection('teams')
            .doc(teamId)
            .collection('metadata')
            .doc('info')
            .get();
        if (metaSnap.exists) {
            const meta = metaSnap.data() || {};
            if (meta.teamName && String(meta.teamName).trim()) {
                return String(meta.teamName).trim();
            }
            if (meta.name && String(meta.name).trim()) {
                return String(meta.name).trim();
            }
        }
    } catch (err) {
        console.warn(`[sendChatNotification] metadata read failed for ${teamId}:`, err.message);
    }

    try {
        const rootSnap = await db.collection('teams').doc(teamId).get();
        if (rootSnap.exists) {
            const data = rootSnap.data() || {};
            if (data.teamName && String(data.teamName).trim()) {
                return String(data.teamName).trim();
            }
            if (data.name && String(data.name).trim()) {
                return String(data.name).trim();
            }
        }
    } catch (err) {
        console.warn(`[sendChatNotification] root team read failed for ${teamId}:`, err.message);
    }

    return 'Your Team';
}

/**
 * Trigger: new chat message under teams/{teamId}/messages/{messageId}
 * Sends FCM push to all other members that have registered device tokens.
 */
exports.sendChatNotification = onDocumentCreated('teams/{teamId}/messages/{messageId}', async (event) => {
    try {
        const snapshot = event.data;
        if (!snapshot) {
            console.log('[sendChatNotification] No data associated with the event');
            return null;
        }

        const message = snapshot.data() || {};
        const teamId = event.params.teamId;
        const messageId = event.params.messageId;
        const senderId = message.userId || message.senderId || null;
        const senderLabel =
            (message.senderName && String(message.senderName).trim()) ||
            (senderId ? String(senderId).substring(0, 8) : 'Someone');
        const textPreview = (message.text && String(message.text).trim()) || 'New message';

        console.log(`[sendChatNotification] New message in team ${teamId} from ${senderLabel} (id=${messageId})`);

        const db = getFirestore();
        const teamName = await resolveTeamName(db, teamId);
        console.log(`[sendChatNotification] Team title: ${teamName}`);

        // All team members' FCM tokens except the sender
        const membersSnapshot = await db
            .collection('teams')
            .doc(teamId)
            .collection('members')
            .get();

        const tokens = [];
        const tokenToMemberMap = {};
        let memberCount = 0;
        let skippedSender = 0;

        membersSnapshot.forEach((docSnap) => {
            memberCount += 1;
            const member = docSnap.data() || {};
            const memberId = docSnap.id;

            // Don't notify the sender (member doc id is the Firebase uid used in chat)
            if (senderId && (memberId === senderId || member.uid === senderId)) {
                skippedSender += 1;
                return;
            }

            if (member.fcmTokens && Array.isArray(member.fcmTokens)) {
                member.fcmTokens.forEach((token) => {
                    if (token && typeof token === 'string' && !tokenToMemberMap[token]) {
                        tokens.push(token);
                        tokenToMemberMap[token] = memberId;
                    }
                });
            } else if (member.fcmToken && typeof member.fcmToken === 'string') {
                // Legacy single-token field
                if (!tokenToMemberMap[member.fcmToken]) {
                    tokens.push(member.fcmToken);
                    tokenToMemberMap[member.fcmToken] = memberId;
                }
            }
        });

        console.log(
            `[sendChatNotification] Members=${memberCount}, skippedSender=${skippedSender}, tokens=${tokens.length}`
        );

        if (tokens.length === 0) {
            console.log('[sendChatNotification] No tokens to send to — ensure each device opened Chat once');
            return null;
        }

        const title = `💬 ${teamName}`;
        const body = `${senderLabel}: ${textPreview}`.substring(0, 180);

        // FCM multicast (max 500 tokens per call; team chat is far smaller)
        const response = await getMessaging().sendEachForMulticast({
            tokens,
            notification: {
                title,
                body
            },
            data: {
                teamId: String(teamId),
                messageId: String(messageId),
                type: 'chat_message'
            },
            android: {
                priority: 'high',
                notification: {
                    sound: 'default',
                    channelId: 'general'
                }
            },
            apns: {
                headers: {
                    'apns-priority': '10'
                },
                payload: {
                    aps: {
                        alert: {
                            title,
                            body
                        },
                        sound: 'default',
                        badge: 1
                    }
                }
            }
        });

        console.log(
            `[sendChatNotification] Sent. Success=${response.successCount}, Failure=${response.failureCount}`
        );

        // Clean up invalid / unregistered tokens only (not transient / config errors)
        const failedTokens = [];
        response.responses.forEach((resp, idx) => {
            if (!resp.success) {
                const code = resp.error?.code || 'unknown';
                const msg = resp.error?.message || '';
                console.error('[sendChatNotification] Failure for token', tokens[idx]?.substring(0, 12), code, msg);

                // APNs key/cert missing or invalid in Firebase Console → iOS delivery always fails.
                if (code === 'messaging/third-party-auth-error') {
                    console.error(
                        '[sendChatNotification] APNs auth failed. Upload an APNs Authentication Key (.p8) in ' +
                        'Firebase Console → Project settings → Cloud Messaging → Apple app configuration. ' +
                        'Development builds need a key that covers the App ID com.andrewestherhuysen.turftime.'
                    );
                }

                if (
                    code === 'messaging/invalid-registration-token' ||
                    code === 'messaging/registration-token-not-registered'
                ) {
                    failedTokens.push(tokens[idx]);
                }
            }
        });

        if (failedTokens.length === 0) {
            return null;
        }

        console.log(`[sendChatNotification] Cleaning up ${failedTokens.length} invalid token(s)`);

        const memberTokensToRemove = {};
        failedTokens.forEach((token) => {
            const memberId = tokenToMemberMap[token];
            if (!memberId) return;
            if (!memberTokensToRemove[memberId]) {
                memberTokensToRemove[memberId] = [];
            }
            memberTokensToRemove[memberId].push(token);
        });

        const batch = db.batch();
        for (const [memberId, tokensToRemove] of Object.entries(memberTokensToRemove)) {
            const memberRef = db.collection('teams').doc(teamId).collection('members').doc(memberId);
            const memberDoc = await memberRef.get();
            if (!memberDoc.exists) continue;

            const memberData = memberDoc.data() || {};
            if (memberData.fcmTokens && Array.isArray(memberData.fcmTokens)) {
                const updatedTokens = memberData.fcmTokens.filter((t) => !tokensToRemove.includes(t));
                batch.update(memberRef, { fcmTokens: updatedTokens });
            } else if (memberData.fcmToken && tokensToRemove.includes(memberData.fcmToken)) {
                batch.update(memberRef, { fcmToken: FieldValue.delete() });
            }
        }

        await batch.commit();
        console.log(
            `[sendChatNotification] Cleaned invalid tokens from ${Object.keys(memberTokensToRemove).length} member(s)`
        );

        return null;
    } catch (error) {
        console.error('[sendChatNotification] Error sending notification:', error);
        return null;
    }
});

console.log('💬 Chat notification function loaded successfully (2nd Gen)');

/**
 * Callable function: requestAdminCodeEmail
 *
 * Called by a team creator to trigger an email with their admin recovery reminder.
 * The plain-text admin code is NEVER stored in Firestore — only its SHA-256 hash is.
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
            console.log(`[requestAdminCodeEmail] No creatorEmail stored for team ${teamId} — skipping email`);
            return { status: 'sent', teamName };
        }

        // Write to the 'mail' collection — requires the Firebase "Trigger Email" extension
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
