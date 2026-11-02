FROM node:22-alpine
WORKDIR /app
COPY package*.json ./
RUN npm ci --omit=dev
COPY src ./src
COPY public ./public
# Fail early if setup was not built before assembling the server image.
COPY public/downloads/RemotePrint.Setup.exe ./public/downloads/RemotePrint.Setup.exe
ENV NODE_ENV=production PORT=7443 HOST=0.0.0.0 DATA_DIR=/app/data
RUN mkdir -p /app/data /app/tunnel-state && chown -R node:node /app
VOLUME ["/app/data"]
EXPOSE 7443
USER node
CMD ["node", "src/index.js"]

