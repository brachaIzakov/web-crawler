# Build context is the repo root (see docker-compose.yml).
FROM node:22-alpine AS build
WORKDIR /app

# Extra CA certificates in ./certs/*.crt (corporate TLS-inspecting proxies) are honoured by npm.
COPY certs/ /certs/
RUN touch /certs/bundle.pem && (cat /certs/*.crt >> /certs/bundle.pem 2>/dev/null || true)
ENV NODE_EXTRA_CA_CERTS=/certs/bundle.pem

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund --loglevel=error
COPY frontend/ .
RUN npm run build

FROM nginx:1.27-alpine
COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
