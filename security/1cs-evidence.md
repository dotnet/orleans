# Orleans security evidence matrix

This matrix maps public repository evidence to common security-assessment areas.
It identifies implemented mechanisms and documentation. Restricted operational
attestations remain manual evidence and are not inferred from public
configuration.

## Evidence quality

- **High:** implementation, test, workflow configuration, or public run result.
- **Medium:** current product or operational guidance.
- **Low:** inferred state or dynamic metadata without an approval record.

## Public evidence matrix

| Requirement | Applicability | Evidence | Quality | Proposed owner | Status | Remediation and suggested response |
|---|---|---|---|---|---|---|
| Threat modeling | Framework and release system | Validated `orleans.tm7` in the approved internal Orleans Azure DevOps repository; `security/threat-model.md`; `docs/site/src/content/docs/security/index.md` | High/Medium | Security champion and runtime architect | Partial | Set the Service Tree link, record reviewer approval, and link residual-risk work. Response: Orleans has a tool-native threat model covering its runtime and delivery boundaries, with full source and evidence mapping; formal review and threat disposition remain pending. |
| Authentication and authorization | Workload connections and grain operations | `docs/site/src/content/docs/security/authentication-authorization.md`; `docs/site/src/content/docs/host/transport-layer-security.md` | High/Medium | Connections-security owner and application security owner | Partial | Attach deployed identity/access evidence. Response: Orleans supplies TLS/mTLS and call-filter mechanisms; applications authenticate subjects and authorize operations. |
| Secret management | CI/release identities and provider configuration | `docs/site/src/content/docs/security/secrets.md`; `.azure/pipelines/templates/build.yaml` | Medium | Release engineering and service-connection owners | Partial | Attach secret inventory, owner, privilege, source, expiry, rotation, scan disposition, and exceptions. Response: source contains no intended production credentials; protected service connections and workload identity supply operational credentials, with inventory and rotation attested separately. |
| Cryptography and TLS | Optional Orleans transport capability | `src/Orleans.Connections.Security`; `test/Orleans.Connections.Security.Tests`; TLS guidance | High | Connections-security owner | Partial | Public library configuration is ready. Applications attest where TLS/mTLS is enabled and how certificates are issued, stored, rotated, and revoked. Response: Orleans supports server-authenticated TLS and mTLS with platform cryptography, TLS 1.2/1.3 defaults, certificate validation, and tested certificate modes. |
| Input validation | Message framing, serialization, and application data | `docs/site/src/content/docs/security/serialization.md`; [PR #10340](https://github.com/dotnet/orleans/pull/10340); [PR #10424](https://github.com/dotnet/orleans/pull/10424) | High/Medium | Serialization owner and API owners | Partial | Attach malformed-input, allocation, fuzzing, authorization-denial, and application-domain validation evidence. Response: Orleans bounds message framing, restricts type-name resolution by default, and validates selected payload-sized allocations; applications validate domain data after deserialization. |
| Dependency and component governance | NuGet, npm, containers, actions, and toolchains | `Directory.Packages.props`; `NuGet.Config`; `.github/dependabot.yml`; dependency graph | High | Component-governance owner | Partial | Attach Component Governance status, SBOM, license review, vulnerability SLA, exceptions, and complete ecosystem coverage. Response: dependencies are centrally versioned and restored from mapped feeds; internal inventory, findings, licenses, and exception closure are attached separately. |
| Code scanning | C#, JavaScript/TypeScript, Actions, and official builds | `.github/workflows/codeql.yml`; `.azure/pipelines/build.yaml` | High | Security scanning owner | Partial | Public scanner configuration is ready. Attach current open findings, severity, suppressions, TSA linkage, and SLA compliance. Response: CodeQL scans repository languages on pull requests, main, merge queue, and a weekly schedule; current finding closure is attested separately. |
| Security testing | Runtime, providers, TLS, and malformed data | `.github/workflows/ci.yml`; security test projects; serializer tests | High | Test lead and security champion | Partial | Map threats to tests and attach fuzzing, penetration-test, certificate-rotation, denial, and secret-leak evidence or approved applicability decisions. Response: CI exercises supported platforms and security-sensitive TLS and serialization behavior; additional abuse-case evidence remains to be attached. |
| Build and release integrity | NuGet packages and binaries | `.azure/pipelines`; `sign`; `Directory.Build.props` | High | Release engineering and repository admin | Partial | Attach one official source-SHA-to-tested-and-signed-package record, approval, signature verification, SBOM/provenance, and access review. Response: official packages use reviewed source, 1ES pipelines, publication approval, and MicroBuild signing; final provenance is supplied from an official release run. |
| Logging and monitoring | Runtime capability and repository/release audit | `docs/site/src/content/docs/host/monitoring`; `docs/site/src/content/docs/deployment/production-operations.md` | Medium | Observability owner and consuming-service SRE | Partial | Consumers attest collection, alerts, access, redaction, and retention. Attach GitHub/Azure DevOps audit ownership for repository/release systems. Response: Orleans emits standard .NET logs, metrics, and traces; deployments own protected collection and security alerting. |
| Incident response | Software vulnerabilities and release compromise | `SECURITY.md`; `SUPPORT.md`; MSRC process; operations guidance | Medium | Security response DRI and MSRC liaison | Partial | Attach internal triage RACI, severity SLA, servicing and revocation procedure, exercise evidence, and active-case attestation without restricted details. Response: suspected vulnerabilities route privately through MSRC; internal triage, servicing, escalation, and exercise evidence is restricted. |
| Safe deployment and rollback | Package servicing; consumer deployment is shared responsibility | `docs/site/src/content/docs/migration/deployment-and-rollback.md`; production operations guidance | Medium | Release owner and consuming-service deployment owner | Partial | Attach package unlist/deprecation/servicing procedure and exercise evidence. Consumers attest their health-gated rollout and rollback. Response: Orleans is distributed as packages and documents compatible, health-gated application rollback; package and consumer rollback controls have separate owners. |
| Data handling, retention, and privacy | CI/contributor data; consumer application data | monitoring and disaster-recovery guidance; CI artifact retention configuration | Medium | Privacy/data owner, CI owner, and consumer data owner | Partial | Confirm repository and assessment scope. Attach CI log/dump classification, access, retention, deletion, and processor-location evidence. Response: no public repository evidence indicates that the repository or release system ingests M365 customer content; applications own data processed through Orleans, while CI data is attested separately. |

## Recent security changes

| Date | Change | Public evidence | Evidence use |
|---|---|---|---|
| 2026-08-05 | Serialization allocation hardening validates remaining input before selected payload-sized allocations. | [dotnet/orleans#10340](https://github.com/dotnet/orleans/pull/10340) | Input-validation and resource-exhaustion evidence. |
| 2026-08-06 | Azure deployment samples added hardened identity, network, and storage defaults. | [dotnet/orleans#10327](https://github.com/dotnet/orleans/pull/10327) | Deployment guidance; not proof of deployed resource state. |
| 2026-08-10 | Serializer type-trust behavior and tests were clarified. | [dotnet/orleans#10424](https://github.com/dotnet/orleans/pull/10424) | Fail-closed type authorization evidence. |
| 2026-08-12 | Dependency and code-scanning findings were remediated. | [dotnet/orleans#10516](https://github.com/dotnet/orleans/pull/10516) | Component-governance remediation evidence. |
| 2026-08-18 | Security boundaries and production guidance were consolidated. | [dotnet/orleans#10554](https://github.com/dotnet/orleans/pull/10554) | Current public security guidance. |
| 2026-08-18 | App Service sample added role enforcement and bounded principal-header validation. | [Azure-Samples App Service #13](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-App-Service/pull/13) | Public sample authorization remediation. |
| 2026-08-18 | Container Apps sample bounded activation keys and shortened inactive collection. | [Azure-Samples Container Apps #18](https://github.com/Azure-Samples/Orleans-Cluster-on-Azure-Container-Apps/pull/18) | Public resource-exhaustion remediation. |
| Current configuration | Maintained Azure deployment samples set Shared Key access to disabled by default after migration. | `samples/Deployment/AzureContainerApps/Azure/storage.bicep`; `samples/Deployment/AzureAppService/infra/flex/storage.bicep`; App Service sample migration guidance | Configuration-intent evidence. Deployed non-production resource state requires an authorized policy or resource export. |
| 2026-08-19 | Connection-authentication work adds provider-neutral, fail-closed client and silo authentication with tests and guidance. | [dotnet/orleans#10496](https://github.com/dotnet/orleans/pull/10496) | Proposed capability; record as in progress until merged and released. |

## Operational evidence

| Area | Public evidence | Assessment |
|---|---|---|
| Supported versions and servicing | Releases, active servicing branches, migration guidance, and `SUPPORT.md`. | Partial. An approved support matrix, EOL dates, and backport policy require owner attestation. |
| Security reporting | `SECURITY.md` routes reports to MSRC. | Ready for public intake evidence; private triage and SLA evidence remains manual. |
| Release and package signing | Official pipeline configuration, MicroBuild signing, publication approval, and signing manifests. | Partial until an official release record proves source, tests, artifact digest, signature, approval, and publication. |
| Branch protection and review | Public CODEOWNERS and repository rules metadata. | Partial. Effective organization rules, bypass actors, access review, and approval settings require attestation. |
| CodeQL and component governance | CodeQL workflow and public dependency metadata. | CodeQL configuration is ready; current findings and internal Component Governance state remain manual. |
| Dependency updates | Central package management and automated SDK updates. | Partial ecosystem coverage. |
| Secret scanning | Repository security features and remediation history. | Restricted alert disposition and rotation evidence remain manual. |
| CI/CD identities and permissions | Scoped GitHub workflow permissions and Azure service connections are configured. | Identity type, privilege, rotation, fork isolation, and signing/publishing access require manual evidence. |
| Production deployment controls | Orleans publishes deployment, readiness, recovery, and rollback guidance. | Consumer-specific. Orleans package publication controls are assessed separately. |
| Monitoring and alerting | Runtime logs, metrics, traces, health, and recommended alerts are documented. | Capability evidence, not a deployed monitoring attestation. |
| Incident response | MSRC intake and operations guidance exist. | Private ownership, exercise, case handling, and communications evidence remain manual. |

## Publicly discoverable historical evidence

- No completed 1CS baseline, `.tm4`, `.tm7`, formal security DFD,
  penetration-test report, SDL approval, or security exception was found in the
  accessible repository, history, visible refs, or public GitHub records.
- The 1ES SDL autobaseline work in
  [#10297](https://github.com/dotnet/orleans/pull/10297) and
  [#10298](https://github.com/dotnet/orleans/pull/10298) configures scan baseline
  updates; it does not establish a completed 1CS product baseline.
- Publicly named security-review branches are investigation residue, not
  approval evidence.
- Public advisory results do not represent private advisories or MSRC cases.

## Confidential and manual evidence

Collect these records only in approved restricted systems:

1. Completed threat-model review record and approval trail.
2. Active MSRC case metadata: owner, status, dates, and control linkage without
   vulnerability details.
3. Prior 1CS assessment IDs, scope, approval, and parent-product inheritance.
4. Current CodeQL, TSA, secret-scanning, Component Governance, SBOM, and
   vulnerability-SLA state.
5. Repository, Azure DevOps, feed, signing, and publication access reviews.
6. Secret and service-connection inventory with rotation evidence.
7. Official release provenance and signature verification.
8. Incident-response exercise and package rollback evidence.
9. Azure resource-state or policy evidence for Shared Key-disabled storage.
10. Data classification, retention, access, deletion, and privacy attestations.

## Ranked gaps

| Rank | Gap | Severity | Effort | Proposed owner |
|---|---|---|---|---|
| 1 | Link the validated Orleans `.tm7` from Service Tree and obtain formal review and threat disposition. | Blocking | Small/Medium | Security champion and runtime architect |
| 2 | Determine whether an approved Orleans 1CS baseline exists; otherwise complete a baseline. | Blocking | Medium | Service owner and 1CS assessment owner |
| 3 | Triage restricted secret-scanning and active vulnerability evidence. | Critical if confirmed | Small/Medium | Repository admin and MSRC liaison |
| 4 | Prove that published packages are the exact tested artifacts and attach signing/provenance evidence. | High | Medium/Large | Release engineering |
| 5 | Publish approved support, vulnerability-servicing, incident, and package-rollback procedures. | High | Medium | Product, servicing, and security owners |
| 6 | Require and attest scanning, dependency review, immutable pinning, complete dependency coverage, SBOM, and provenance. | High | Medium/Large | Supply-chain and scanning owners |
| 7 | Attach access, identity, secret, signing, feed, monitoring, privacy, and exercise evidence. | High | Medium | Control operators |
| 8 | Add connection, serializer, authorization, tenant-isolation, diagnostic-leak, and provider-tampering security regression evidence. | Medium/High | Large | Runtime, test, and application security owners |

## Assessment disposition

The public evidence mapping and validated internal `.tm7` are ready for
reviewer use. Final submission remains blocked by the Service Tree link,
assessment-cycle decision, restricted control evidence, threat disposition, and
formal owner and reviewer attestations.
