# Security Policy

## Supported versions

Security fixes are released for the latest version only.

## Reporting a vulnerability

Please do not report security problems in public issues, pull requests, or discussions.

Report them privately through GitHub's
[private vulnerability reporting](https://github.com/sharp-space/mediator/security/advisories/new): the
**Report a vulnerability** button under this repository's **Security** tab. Include the affected package and version,
a minimal reproduction, and the impact you see.

You can expect an acknowledgement within a few days. The fix is released together with a security advisory that
credits you, unless you prefer otherwise.

## Security-relevant areas

- Middleware that does not run. Applications enforce validation and authorization in middleware, so a message that
  reaches its handler without passing through the middleware registered for it, in any assembly and under the JIT or
  Native AOT, is a vulnerability.
- Validation. With `Sharp.Mediator.Validation`, a request reaches its handler only when no validator reports an
  `Error`-severity entry. A request that gets through despite one is a vulnerability.
