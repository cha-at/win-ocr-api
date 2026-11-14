# Security policy

## Supported versions

| Version | Supported |
| --- | --- |
| 0.1.x | ✅ |

## Reporting a vulnerability

Please do **not** open a public issue for a vulnerability. Use GitHub's
[*Report a vulnerability*](https://github.com/cha-at/win-ocr-api/security/advisories/new) (private security
advisory). Include the version from `GET /health`, the affected endpoint, the request that triggers it, and what an
attacker gains. Expect a first reply within a week.

## Threat model

This project is a local helper, not a hardened network service. Its intended deployment is a single Windows machine,
reachable at `127.0.0.1` by the same user. Anything that breaks that assumption is worth knowing about:

- **No authentication by default.** Unless `OCR_API_TOKEN` is set, every endpoint is open to anyone who can reach
  the port.
- **Weak authentication when enabled.** The token is compared as a plain string (constant-time at the sequence level,
  but not a hardened scheme), there is no rate limiting, no lockout and no TLS.
- **Server-side request forgery.** `?url=` makes the server fetch an arbitrary URL, including `http://localhost:…`
  and cloud metadata addresses. Do not enable it for untrusted clients.
- **Resource exhaustion.** Requests are buffered in memory up to 64 MB and OCR is CPU/NPU bound; concurrent uploads
  can exhaust memory or starve the recognizer. There is no queue limit, quota or per-client throttling.
- **File writes.** The service never writes request data to disk. Uploads, decoded bitmaps and results stay in
  memory, which also means a crash loses everything.
- **Dependencies.** Image decoding and OCR run through Windows components (`Windows.Media.Ocr`,
  `BitmapDecoder`, Windows App SDK); the service trusts them to parse hostile images safely.
- **Package identity.** `package/register.ps1` registers a sparse package for the current user; the self-signed
  certificate from `package/build-msix.ps1` is a development artifact and must never be trusted outside your own
  machine. Treat the generated `.pfx` password as disposable.

## Recommendations for users

1. Leave the default binding at `127.0.0.1`.
2. Set `OCR_API_TOKEN` if any other local account or process can reach the port.
3. Do not expose the port to the internet or to a LAN you do not control. If you must, put it behind a reverse proxy
   that terminates TLS and enforces authentication and rate limits.
4. Do not accept attacker-controlled `?url=` values from untrusted clients.
