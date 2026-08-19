# Orleans security review package

This directory contains the public, repository-verifiable source material for
an Orleans security assessment. It separates implemented Orleans behavior from
deployment responsibilities and from evidence that exists only in restricted
Microsoft systems.

## Contents

- [Threat model](threat-model.md): scope, architecture, data flows, trust
  boundaries, STRIDE analysis, abuse cases, mitigations, and residual risks.
- [Security evidence](1cs-evidence.md): public control evidence, recent security
  changes, evidence quality, gaps, and proposed control owners.

## Review boundary

The review covers Microsoft-owned Orleans source, published NuGet packages, and
the repository build and release configuration. It includes client-to-gateway
and silo-to-silo connections, serialization, membership, directories,
reminders, providers, diagnostics, CI/CD, signing, and package publication.

Applications built with Orleans remain separate security subjects. They own
their users, tenant model, authorization policy, network exposure, provider
accounts, secrets, production deployment, monitoring, data handling, and
incident response. Orleans owns the framework mechanisms and documentation
that those applications use.

## Evidence handling

Public files contain source, documentation, tests, workflows, and public pull
request evidence. Active vulnerability cases, internal scan results, access
reviews, service identities, signing-key custody, private incident records, and
assessment approvals belong in approved restricted systems.

## TM7 status

A real `orleans.tm7` was created with Microsoft Threat Modeling Tool
`7.3.51110.1` and published to the approved internal Orleans Azure DevOps
repository. The model contains 20 components and trust boundaries, 15 numbered
data flows, and 60 tool-generated threats. It was saved by the tool, closed, and
reopened successfully.

The internal repository includes this authoring source and the evidence matrix.
Its Git URL belongs in the Orleans Service Tree threat-model metadata and the
restricted assessment. Formal reviewer approval and threat disposition remain
pending.

## Update triggers

Review this package for every material change to connection authentication,
serialization, membership or directory protocols, provider authorization,
administrative endpoints, package signing, publication, or tenant-isolation
guidance, and at the cadence required by the governing assessment.
