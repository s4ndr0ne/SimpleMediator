# Security Policy

## Supported versions

Only the latest minor release receives security fixes.

| Version | Supported |
| ------- | --------- |
| 4.x (latest) | ✅ |
| < 4.0 | ❌ |

## Reporting a vulnerability

Please do **not** open a public issue for security problems.

Report vulnerabilities through GitHub's private vulnerability reporting:
<https://github.com/s4ndr0ne/SimpleMediator/security/advisories/new>

Include: the affected version, the target framework, a description of the impact, and a
minimal reproduction. You will receive an acknowledgment as soon as possible, and credit
in the release notes if you wish.

## Scope notes

SimpleMediator is a dispatch library: it constructs and invokes user-registered handlers
through Microsoft.Extensions.DependencyInjection. Assembly scanning and generic type
construction are documented as not compatible with trimming and Native AOT; running it in
those modes is a build-configuration issue, not a vulnerability. Issues in dependencies
are tracked through Dependabot and the CI vulnerability check.
