# @athar/collab

Hocuspocus sidecar, the single writer of each live document. Aspire runs `npm run dev` with `PORT`, `WEB_URL` and `COLLAB_SECRET` set. Every permission decision goes through web's internal collab API.

`scripts/at-clients.mjs` holds the Yjs clients for AT-10, AT-11 and AT-12. `tests/Athar.IntegrationTests/CollabTests.cs` runs them against the AppHost.
