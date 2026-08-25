FROM node:24-alpine AS dependencies
WORKDIR /app
COPY src/kidtime-web/package.json src/kidtime-web/package-lock.json ./
RUN npm ci

FROM node:24-alpine AS build
WORKDIR /app
# Next.js resolves the base path at build time, so it has to be known before npm run build.
ARG NEXT_PUBLIC_BASE_PATH=""
ENV NEXT_PUBLIC_BASE_PATH=$NEXT_PUBLIC_BASE_PATH
COPY --from=dependencies /app/node_modules ./node_modules
COPY src/kidtime-web .
RUN npm run build

FROM node:24-alpine
WORKDIR /app
ENV NODE_ENV=production
ENV PORT=3000
RUN addgroup -S kidtime && adduser -S kidtime -G kidtime
COPY --from=build --chown=kidtime:kidtime /app/.next/standalone ./
COPY --from=build --chown=kidtime:kidtime /app/.next/static ./.next/static
COPY --from=build --chown=kidtime:kidtime /app/public ./public
USER kidtime
EXPOSE 3000
CMD ["node", "server.js"]
