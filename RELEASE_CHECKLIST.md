# Publication and free-signing gates

The Apache-2.0 native core has a downloadable [unsigned portable preview](https://github.com/TorofSkiy/cino-aos-windows-native/releases/tag/v0.4.0-preview.1). This is not a signed installer or a production certification.

| Gate | Evidence / remaining action |
| --- | --- |
| Scope and license | Authorized public native-core source; Apache-2.0; private deployment components excluded |
| Source and dependencies | Source membership and hash checks; pinned SDK and NuGet lockfiles; 34-package dependency inventory |
| Public build | [Run 37577807061](https://github.com/TorofSkiy/cino-aos-windows-native/actions/runs/37577807061) succeeded: 9 workspace tests and 6 actual isolated Host checks |
| Downloadable release | v0.4.0-preview.1, five assets, uploaded bytes downloaded and hash-checked before publication |
| Roles | TorofSkiy is committer, reviewer and proposed human signing approver; see Code signing policy |
| MFA | Required for all repository and SignPath users; configure SignPath MFA during onboarding |
| Reputation | Early-stage project with one public binary preview; established adoption has not been verified |
| Signing application | Owner must submit truthful project, release and contact information; acceptance is external and unconfirmed |
| Signing integration | SignPath account, GitHub App, artifact rules and certificate are not configured |
| Physical target | Signed installation, real target tasks, network update and rollback remain unverified for this release |
| Cost | Standard public Windows runner; Release assets; no Actions artifact/cache storage in the release workflow; no paid resources |

The portable package includes usage, privacy and removal instructions. The private network installer and management backend are not part of this public core. A sponsorship for the core would not authorize signing private components. Manual approval is required for each future signing request.

References: [SignPath conditions](https://signpath.org/terms.html), [apply](https://signpath.org/apply.html), [GitHub build provenance integration](https://docs.signpath.io/trusted-build-systems/github).
