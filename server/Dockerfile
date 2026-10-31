FROM node:22-alpine
WORKDIR /app
COPY package*.json ./
RUN npm ci --omit=dev
COPY src ./src
COPY public ./public
ENV NODE_ENV=production PORT=7443 HOST=0.0.0.0 DATA_DIR=/app/data
RUN mkdir -p /app/data /app/tunnel-state && chown -R node:node /app
VOLUME ["/app/data"]
EXPOSE 7443
USER node
CMD ["node", "src/index.js"]

