# Threat Model

## Assets

- Client inventory reports with host names, OS data, Office data, installed software, and activation facts.
- Server report store under `ProgramData`.
- Windows Service identities on client and server hosts.
- Optional shared token used by clients in `X-Inventory-Token`.

## Trust Boundaries

- Client host to server HTTP listener.
- Server HTTP listener to local report storage.
- Browser user to dashboard.
- Installer scripts to Windows Service Control Manager and firewall configuration.

## Attacker-Controlled Inputs

- HTTP request body sent to `POST /api/v1/inventory`.
- HTTP headers, including missing or invalid `X-Inventory-Token`.
- Computer names and software names inside submitted JSON.
- Local command-line parameters passed during installation.

## Required Invariants

- Clients must not export product keys, credentials, or user documents.
- Server must store reports as data files and must not execute report content.
- Dashboard must treat report fields as untrusted display data.
- Operators must restrict server exposure with firewall rules, listener scope, token checks, or a reverse proxy.
- Service install scripts must not delete unrelated files or modify unrelated services.

## Main Risks

- Unauthorized report submission can poison inventory data.
- Unrestricted dashboard access can expose asset and software inventory.
- Plain HTTP can expose reports on untrusted networks.
- A broad listener prefix such as `http://+:8080/` can expose the service on more interfaces than intended.

## Controls

- Use `-Token` on server and client installation.
- Prefer a host-specific listener prefix or firewall scope for production.
- Use HTTPS termination or a reverse proxy outside trusted LAN segments.
- Keep the server `DataPath` writable only by the server service identity and administrators.
- Review generated JSON before sharing it outside the organization.
