# Stub Torznab upstream for the E2E golden path (arb-rga.7).
#
# No package.json and no npm install: server.js imports nothing but node:http, so there is
# nothing to resolve and no lockfile to drift. A dependency here would be a supply-chain
# surface inside the merge gate for no benefit.
FROM node:22-alpine

WORKDIR /app
COPY server.js ./

# Runs unprivileged. The node image ships a `node` user; the stub needs no write access at
# all -- its fixtures are compiled into the source file.
USER node

EXPOSE 5100

CMD ["node", "server.js"]
