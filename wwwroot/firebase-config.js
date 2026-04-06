// Import the functions you need from the SDKs you need
import { initializeApp } from "firebase/app";
import { getAnalytics } from "firebase/analytics";
import { getFirestore } from "firebase/firestore";
import { getAuth } from "firebase/auth";
import { getStorage } from "firebase/storage";

// Your web app's Firebase configuration
// For Firebase JS SDK v7.20.0 and later, measurementId is optional
const firebaseConfig = {
    apiKey: "AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk",
    authDomain: "turf-timer.firebaseapp.com",
    projectId: "turf-timer",
    storageBucket: "turf-timer.firebasestorage.app",
    messagingSenderId: "846410659178",
    appId: "1:846410659178:web:f26efa105cf06cd241a113",
    measurementId: "G-Q6H3LXGZ8B"
};

// Initialize Firebase
const app = initializeApp(firebaseConfig);

// Initialize Firebase services
const analytics = getAnalytics(app);
const db = getFirestore(app);
const auth = getAuth(app);
const storage = getStorage(app);

// Export for use in other modules
export { app, analytics, db, auth, storage, firebaseConfig };
const analytics = getAnalytics(app);