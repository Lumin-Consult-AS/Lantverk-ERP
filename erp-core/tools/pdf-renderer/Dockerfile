# Chromium and its system libraries come from the official Playwright image. The image tag and the
# "playwright" version in package.json must match.
FROM mcr.microsoft.com/playwright:v1.49.1-noble
WORKDIR /app
COPY package.json ./
RUN npm install --omit=dev
COPY server.js ./
ENV PORT=3000
EXPOSE 3000
# Run as the unprivileged user the image provides, not root.
USER pwuser
CMD ["node", "server.js"]
