# Threat model — [Feature]
Owner: | Date: | Requirement IDs: | Data classification: | Reviewers:

## Data flows and trust boundaries
Actors, browser, gateway, API, workers, broker, database, vault, on-prem connector, target system. Include credentials, personal data and approval evidence.

## Assets and security objectives
Confidentiality, integrity, availability, nonrepudiation, least privilege, audit completeness.

## STRIDE threats and mitigations
| Threat | Entry point | Impact | Mitigation | Test | Owner | Status |
|---|---|---|---|---|---|---|
| Spoofing | | | | | | |
| Tampering | | | | | | |
| Repudiation | | | | | | |
| Information disclosure | | | | | | |
| Denial of service | | | | | | |
| Elevation of privilege | | | | | | |

## Abuse cases
- Requester attempts own approval or maker/checker collusion.
- Admin creates wildcard matrix to route privileged access to self.
- Stale HR attributes bypass controls.
- Connector returns partial discovery; system falsely revokes accounts.
- Compromised browser submits another user's entitlement request.
- Replayed queue message provisions access twice.

## Residual risk and approvals
Risks, severity, accepted exceptions, reviewer evidence, expiry and follow-up.
