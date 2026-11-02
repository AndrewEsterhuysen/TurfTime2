# Cloud Run image (not deployed in Phase 1).
FROM node:20-slim AS build
WORKDIR /app
COPY package*.json tsconfig.json ./
RUN npm ci
COPY src ./src
COPY test ./test
RUN npx tsc -p tsconfig.json && npm prune --omit=dev

FROM node:20-slim
WORKDIR /app
ENV NODE_ENV=production FIREBASE_PROJECT_ID=turf-timer
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/build/src ./build/src
COPY package.json ./
USER node
CMD ["node", "build/src/index.js"]
