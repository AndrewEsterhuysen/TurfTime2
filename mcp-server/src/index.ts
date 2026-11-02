import { createApp } from "./app.js";
import { defaultVerifier } from "./auth.js";

const projectId = process.env.FIREBASE_PROJECT_ID ?? "turf-timer";
const port = Number(process.env.PORT ?? 8080);
const app = createApp({ projectId, verify: await defaultVerifier(projectId) });
app.listen(port, () => console.log(JSON.stringify({ kind: "startup", projectId, port })));
