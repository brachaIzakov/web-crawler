# Extra CA certificates (optional)

Drop PEM-encoded certificates with a `.crt` extension here if your network uses a TLS-inspecting
proxy with a private root CA. They are trusted during image builds (NuGet, npm) and by the API and
worker at runtime (the worker's HttpClient), so HTTPS sites behind the proxy can be crawled.

The directory is empty by default and nothing changes when it stays empty.
