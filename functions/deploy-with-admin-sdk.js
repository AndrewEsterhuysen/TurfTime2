// Direct deployment using Firebase Admin SDK
// This bypasses Firebase CLI auth issues

const admin = require('firebase-admin');
const { execSync } = require('child_process');
const path = require('path');

// Path to service account key
const serviceAccountPath = process.env.SERVICE_ACCOUNT_KEY || 
    'C:\\Users\\esterha\\Downloads\\turf-timer-b1a2dbcba9cf.json';

console.log('🔑 Using service account:', serviceAccountPath);

// Initialize Firebase Admin
const serviceAccount = require(serviceAccountPath);
admin.initializeApp({
    credential: admin.credential.cert(serviceAccount),
    projectId: 'turf-timer'
});

console.log('✅ Firebase Admin initialized');
console.log('📂 Project ID:', admin.app().options.projectId);

// Now try to deploy using Firebase CLI with the initialized credentials
console.log('🚀 Attempting deployment...');

try {
    const result = execSync(
        'firebase deploy --only functions --project turf-timer',
        {
            cwd: path.resolve(__dirname),
            env: {
                ...process.env,
                GOOGLE_APPLICATION_CREDENTIALS: serviceAccountPath,
                GCLOUD_PROJECT: 'turf-timer',
                FIREBASE_PROJECT: 'turf-timer'
            },
            stdio: 'inherit'
        }
    );

    console.log('✅ Deployment successful!');
} catch (error) {
    console.error('❌ Deployment failed:', error.message);
    process.exit(1);
}
