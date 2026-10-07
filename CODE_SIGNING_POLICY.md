# Code signing policy — proposal

Status: no code-signing service approved; no SignPath Foundation sponsorship claimed.

Only public first-party source in this repository is proposed for free OSS signing. Publication of this source snapshot under Apache-2.0 has been authorized. Models, private configuration, credentials and separately owned components are excluded. No commercial dual-license arrangement is offered for this component.

Project roles: [TorofSkiy](https://github.com/TorofSkiy) is the committer, reviewer and proposed human release-signing approver. This is a single-maintainer project; independent review or certification is not claimed. Automated agents cannot approve signing requests. SignPath permissions and manual approval must be configured during onboarding.

Proposed pipeline:
1. Named authors submit changes; a named reviewer reviews the exact diff and dependency changes. Use MFA and protected branches.
2. Build the reviewed commit on a standard GitHub-hosted Windows runner using locked dependencies.
3. Upload only the selected portable build artifact and preserve GitHub-provided build provenance.
4. After SignPath acceptance, connect the reviewed repository through the SignPath GitHub App and configure the approved artifact definition. Only `host/Cino.NativeHost.exe` and `workbench/Cino.Workbench.exe` are first-party binary signing candidates in this preview.
5. A named release approver manually authorizes signing. Retrieve signed output, verify publisher and timestamp, regenerate checksums and test on Windows with its normal protections.
6. Publish only the approved release; a signature is not proof of correct behavior. Keep versioned rollback artifacts.

Do not sign arbitrary uploaded binaries. Do not replace Microsoft's runtime signatures. No auto-approval, public API token, direct push-to-deploy or subscription purchase is configured here.

Before applying for signing: verify the named roles, applicant contact information, release evidence and build provenance in RELEASE_CHECKLIST.md. The repository is https://github.com/TorofSkiy/cino-aos-windows-native and its maintainer is TorofSkiy. If accepted, add the Foundation's exact required attribution and actual signing policy; do not fabricate acceptance.

Sources:
- https://signpath.org/terms.html
- https://signpath.org/apply.html
- https://docs.signpath.io/trusted-build-systems/github

