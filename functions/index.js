const { onDocumentCreated } = require('firebase-functions/v2/firestore');
const { onCall, HttpsError } = require('firebase-functions/v2/https');
const { onSchedule } = require('firebase-functions/v2/scheduler');
const { initializeApp } = require('firebase-admin/app');
const { getFirestore, FieldValue, Timestamp } = require('firebase-admin/firestore');
const { getMessaging } = require('firebase-admin/messaging');

initializeApp();

/** Teams with no activity for this many days are hard-deleted (owner left / abandoned). */
const DORMANT_TEAM_DAYS = 365;

/**
 * Single-controller lock: if the controlling Admin's heartbeat is older than this,
 * the scheduled function clears the lock so another Admin can take over.
 * Must be greater than the client heartbeat interval (currently ~45s).
 */
const CONTROLLER_STALE_MS = 90 * 1000;

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

        const tokenPreview = tokens.map((t) => String(t).substring(0, 14) + '…').join(', ');
        console.log(
            `[sendChatNotification] Members=${memberCount}, skippedSender=${skippedSender}, ` +
            `tokens=${tokens.length} [${tokenPreview}]`
        );

        if (tokens.length === 0) {
            console.log('[sendChatNotification] No tokens to send to — ensure each device opened Chat once');
            return null;
        }

        const title = `💬 ${teamName}`;
        const body = `${senderLabel}: ${textPreview}`.substring(0, 180);

        // FCM multicast (max 500 tokens per call).
        //
        // No top-level `notification` block: that would force Android's system tray to
        // render without our full-color LargeIcon. Instead:
        //   - Android: high-priority *data* message → app posts notification with app icon
        //   - iOS:     APNs alert payload (system shows app icon automatically)
        const response = await getMessaging().sendEachForMulticast({
            tokens,
            data: {
                teamId: String(teamId),
                messageId: String(messageId),
                type: 'chat_message',
                title: String(title),
                body: String(body)
            },
            android: {
                priority: 'high',
                ttl: 3600 * 1000
                // intentionally no android.notification — client builds it with LargeIcon
            },
            // APNs: pure user-visible alert. Do NOT set content-available/mutable-content
            // unless we have a Notification Service Extension — those flags make iOS prefer
            // Notification Center delivery without a banner on many builds.
            apns: {
                headers: {
                    'apns-priority': '10',
                    'apns-push-type': 'alert'
                },
                payload: {
                    aps: {
                        alert: {
                            title,
                            body
                        },
                        sound: 'default',
                        badge: 1,
                        // iOS 15+: normal banner interruption (not passive/quiet)
                        'interruption-level': 'active'
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
 * Callable function: requestAdminRecoveryEmail
 * (renamed from requestAdminCodeEmail to avoid a stuck Cloud Run name collision)
 *
 * Called by a team creator to trigger an email with their admin recovery reminder.
 * The plain-text admin code is NEVER stored in Firestore — only its SHA-256 hash is.
 *
 * Request payload: { teamId: string }
 * Returns: { status: "sent" | "not_found" | "error", teamName?: string }
 */
exports.requestAdminRecoveryEmail = onCall(async (request) => {
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

        console.log(`[requestAdminRecoveryEmail] Recovery reminder requested for team ${teamId} (${teamName}) by uid ${createdBy}`);

        if (!creatorEmail) {
            console.log(`[requestAdminRecoveryEmail] No creatorEmail stored for team ${teamId} — skipping email`);
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

        console.log(`[requestAdminRecoveryEmail] Mail document created for ${creatorEmail}`);
        return { status: 'sent', teamName };
    } catch (error) {
        console.error('[requestAdminRecoveryEmail] Error:', error);
        throw new HttpsError('internal', 'Could not process request.');
    }
});

// ── Dormant team cleanup ─────────────────────────────────────────────────────

/**
 * Best-effort last activity timestamp (ms since epoch) for a team.
 * Uses explicit lastActivityUtc / createdAt when present, else Firestore updateTime
 * on metadata + roster (covers games without extra client fields).
 */
async function resolveLastActivityMs(db, teamId, metaSnap) {
    let latest = 0;
    const meta = metaSnap.exists ? (metaSnap.data() || {}) : {};

    const asMs = (v) => {
        if (!v) return 0;
        if (typeof v.toMillis === 'function') return v.toMillis();
        if (v instanceof Date) return v.getTime();
        if (typeof v === 'string') {
            const t = Date.parse(v);
            return Number.isFinite(t) ? t : 0;
        }
        if (typeof v === 'number' && Number.isFinite(v)) return v;
        return 0;
    };

    latest = Math.max(latest, asMs(meta.lastActivityUtc), asMs(meta.lastActivityAt), asMs(meta.createdAt));
    if (metaSnap.exists && metaSnap.updateTime) {
        latest = Math.max(latest, metaSnap.updateTime.toMillis());
    }

    try {
        const rosterSnap = await db
            .collection('teams')
            .doc(teamId)
            .collection('roster')
            .doc('data')
            .get();
        if (rosterSnap.exists) {
            const r = rosterSnap.data() || {};
            latest = Math.max(latest, asMs(r.lastModifiedUtc), asMs(r.lastModified));
            if (rosterSnap.updateTime) {
                latest = Math.max(latest, rosterSnap.updateTime.toMillis());
            }
        }
    } catch (err) {
        console.warn(`[cleanupDormantTeams] roster read ${teamId}:`, err.message);
    }

    // No signal at all → treat as ancient so orphans get cleaned
    return latest > 0 ? latest : 0;
}

/** Delete all docs in a subcollection (batched). */
async function deleteSubcollection(db, teamId, subName) {
    const col = db.collection('teams').doc(teamId).collection(subName);
    let deleted = 0;
    // eslint-disable-next-line no-constant-condition
    while (true) {
        const page = await col.limit(200).get();
        if (page.empty) break;
        const batch = db.batch();
        page.docs.forEach((d) => batch.delete(d.ref));
        await batch.commit();
        deleted += page.size;
        if (page.size < 200) break;
    }
    return deleted;
}

/**
 * Hard-delete a team tree (admin SDK — bypasses client security rules).
 * Mirrors client owner-delete: invite indexes + members/messages/sessions/logs/public/roster/metadata.
 */
async function hardDeleteTeam(db, teamId, meta) {
    const inviteCode = (meta.inviteCode && String(meta.inviteCode).trim().toUpperCase()) || '';
    const compact = inviteCode.replace(/[^A-Z0-9]/g, '');

    for (const code of [compact, inviteCode]) {
        if (!code) continue;
        try {
            await db.collection('invite_codes').doc(code).delete();
        } catch (err) {
            console.warn(`[cleanupDormantTeams] invite_codes/${code}:`, err.message);
        }
    }

    for (const sub of ['members', 'messages', 'sessions', 'logs', 'public', 'roster', 'metadata']) {
        try {
            const n = await deleteSubcollection(db, teamId, sub);
            if (n > 0) {
                console.log(`[cleanupDormantTeams] ${teamId}/${sub}: deleted ${n} docs`);
            }
        } catch (err) {
            console.warn(`[cleanupDormantTeams] ${teamId}/${sub}:`, err.message);
        }
    }

    // Known singleton paths (if any remain)
    for (const path of [
        ['roster', 'data'],
        ['public', 'invite'],
        ['metadata', 'info'],
    ]) {
        try {
            await db.collection('teams').doc(teamId).collection(path[0]).doc(path[1]).delete();
        } catch (_) {
            /* ignore */
        }
    }

    try {
        await db.collection('teams').doc(teamId).delete();
    } catch (_) {
        /* root teams/{id} often does not exist */
    }
}

/**
 * Scheduled: release stale match controllers (multi-admin single-controller lock).
 *
 * Clients write controllerHeartbeatUtc while holding control. The server — not peers —
 * is authoritative for auto-release so:
 *   - lock clears even if no other Admin app is open
 *   - clients do not need to poll/patch release themselves (less traffic)
 *
 * Schedule: every 1 minute.
 * Deploy: firebase deploy --only functions:releaseStaleGameControllers --project turf-timer
 */
exports.releaseStaleGameControllers = onSchedule(
    {
        schedule: 'every 1 minutes',
        timeZone: 'UTC',
        memory: '256MiB',
        timeoutSeconds: 120,
        retryCount: 0,
    },
    async () => {
        const db = getFirestore();
        const cutoffMs = Date.now() - CONTROLLER_STALE_MS;
        console.log(
            `[releaseStaleGameControllers] Start cutoff=${new Date(cutoffMs).toISOString()} ` +
                `(stale=${CONTROLLER_STALE_MS / 1000}s)`
        );

        // teams/{teamId}/roster/data documents (collection group "roster")
        const rosterGroup = await db.collectionGroup('roster').get();
        let scanned = 0;
        let withController = 0;
        let released = 0;
        let errors = 0;

        for (const doc of rosterGroup.docs) {
            // App stores live state at roster/data only
            if (doc.id !== 'data') continue;
            scanned += 1;

            try {
                const data = doc.data() || {};
                const controllerUid = String(data.controllerUid || '').trim();
                if (!controllerUid) continue;
                withController += 1;

                const hbMs = resolveControllerHeartbeatMs(data);
                if (hbMs > cutoffMs) continue;

                const teamId = doc.ref.parent.parent ? doc.ref.parent.parent.id : '?';
                console.log(
                    `[releaseStaleGameControllers] Releasing team=${teamId} ` +
                        `controller=${controllerUid.substring(0, 8)}… ` +
                        `heartbeat=${hbMs ? new Date(hbMs).toISOString() : 'missing'}`
                );

                await doc.ref.update({
                    controllerUid: '',
                    controllerDisplayName: '',
                    controlRequestUid: '',
                    controlRequestDisplayName: '',
                    controlRequestId: '',
                    controllerHeartbeatUtc: Timestamp.fromMillis(0),
                    lastModifiedUtc: FieldValue.serverTimestamp(),
                });
                released += 1;
            } catch (err) {
                errors += 1;
                console.error(`[releaseStaleGameControllers] Failed ${doc.ref.path}:`, err);
            }
        }

        console.log(
            `[releaseStaleGameControllers] Done scanned=${scanned} withController=${withController} ` +
                `released=${released} errors=${errors}`
        );
        return null;
    }
);

/**
 * Prefer controllerHeartbeatUtc; fall back to lastModifiedUtc for older clients.
 * @returns {number} epoch ms, or 0 if unknown (treated as stale)
 */
function resolveControllerHeartbeatMs(data) {
    const hb = data.controllerHeartbeatUtc;
    if (hb && typeof hb.toMillis === 'function') {
        const ms = hb.toMillis();
        if (ms > 0) return ms;
    }
    if (hb && typeof hb.seconds === 'number') {
        const ms = hb.seconds * 1000;
        if (ms > 0) return ms;
    }
    const lm = data.lastModifiedUtc;
    if (lm && typeof lm.toMillis === 'function') {
        const ms = lm.toMillis();
        if (ms > 0) return ms;
    }
    if (lm && typeof lm.seconds === 'number') {
        const ms = lm.seconds * 1000;
        if (ms > 0) return ms;
    }
    return 0;
}

/**
 * Scheduled: delete shared teams with no roster/metadata activity for 12 months.
 *
 * Covers the case where the owner left without Delete: orphaned cloud teams
 * are removed after a long dormancy window so Firestore does not grow forever.
 *
 * Schedule: daily 03:15 Australia/Sydney (adjust if needed).
 * Deploy: firebase deploy --only functions:cleanupDormantTeams --project turf-timer
 */
exports.cleanupDormantTeams = onSchedule(

    {
        schedule: 'every day 03:15',
        timeZone: 'Australia/Sydney',
        memory: '512MiB',
        timeoutSeconds: 540,
        retryCount: 1,
    },
    async () => {
        const db = getFirestore();
        const cutoffMs = Date.now() - DORMANT_TEAM_DAYS * 24 * 60 * 60 * 1000;
        console.log(
            `[cleanupDormantTeams] Start cutoff=${new Date(cutoffMs).toISOString()} (${DORMANT_TEAM_DAYS} days)`
        );

        // All team metadata/info docs (no root teams/{id} document required)
        const metaGroup = await db.collectionGroup('metadata').get();
        let scanned = 0;
        let dormant = 0;
        let deleted = 0;
        let skipped = 0;
        let errors = 0;

        for (const doc of metaGroup.docs) {
            if (doc.id !== 'info') continue;
            const teamRef = doc.ref.parent.parent;
            if (!teamRef) continue;
            const teamId = teamRef.id;
            scanned += 1;

            try {
                const meta = doc.data() || {};
                if (meta.retainForever === true || meta.skipDormantCleanup === true) {
                    skipped += 1;
                    continue;
                }

                const lastMs = await resolveLastActivityMs(db, teamId, doc);
                if (lastMs > cutoffMs) continue;

                dormant += 1;
                const teamName = meta.teamName || teamId;
                console.log(
                    `[cleanupDormantTeams] Deleting dormant team ${teamId} (${teamName}) ` +
                        `lastActivity=${lastMs ? new Date(lastMs).toISOString() : 'unknown'}`
                );
                await hardDeleteTeam(db, teamId, meta);
                deleted += 1;
            } catch (err) {
                errors += 1;
                console.error(`[cleanupDormantTeams] Failed ${teamId}:`, err);
            }
        }

        console.log(
            `[cleanupDormantTeams] Done scanned=${scanned} dormant=${dormant} ` +
                `deleted=${deleted} skipped=${skipped} errors=${errors}`
        );
        return null;
    }
);
