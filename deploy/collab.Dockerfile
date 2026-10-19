# Athar collab (Hocuspocus sidecar: Yjs sync, persistence through web's internal API). Build from the repository root:
#   docker build -f deploy/collab.Dockerfile -t athar-collab .

FROM node:22-bookworm-slim AS build
WORKDIR /app
COPY src/collab/package.json src/collab/package-lock.json ./
RUN npm ci
COPY src/collab/ ./
RUN npm run build && npm prune --omit=dev

FROM node:22-bookworm-slim
WORKDIR /app
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/dist ./dist
COPY --from=build /app/package.json ./
ENV NODE_ENV=production PORT=1234
USER node
EXPOSE 1234
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 CMD node -e "fetch('http://localhost:1234/').then(r => process.exit(r.status < 500 ? 0 : 1)).catch(() => process.exit(1))"
CMD ["node", "dist/server.js"]
