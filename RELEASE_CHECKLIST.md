# Publication and free-signing gates

Current state: authorized Apache-2.0 source preview in https://github.com/TorofSkiy/cino-aos-windows-native. No binary release, signing application, certificate or target deployment is claimed.

| Gate | Current evidence / action |
| --- | --- |
| Open-source direction | User selected the open-source option; free-only constraint |
| Ownership and license | User authorized public Apache-2.0 publication of this source snapshot |
| Repository owner | TorofSkiy; repository ID 1402685399 |
| Existing reputation/history | Not verified; do not fabricate stars, downloads, releases or contributors |
| Source export | Explicit source allowlist, no original Git history or private deployment directory |
| Dependencies | Exact NuGet lockfiles, license copies and generated inventory |
| Local build/test | See local build receipts; these are not a GitHub CI run |
| CI | Manual workflow, pinned official action commits; consult repository Actions for actual run evidence |
| Maintainers | TorofSkiy maintains the preview; independent review, release approval roles and MFA verification remain signing prerequisites |
| Security reporting | Owner must configure an actual private reporting channel |
| Public preview | Source publication authorized; no signed binaries or production certification implied |
| SignPath application | Submit truthful public project/release URLs only after prerequisites are met |
| Signing | Dependent on external review; no guarantee of acceptance or timetable |
| Physical acceptance | Verify signed installation, target tasks, update and rollback separately |
| Cost | No paid signing or cloud resources; artifact upload disabled by default; verify free storage and a zero spending budget before enabling it |

The source and portable binary packages are review artifacts. The private target-specific network installer and management backend are not part of this first snapshot. Preparing this core is a step toward the network release; it does not unblock the old unsigned installer by itself.

Official references checked during preparation:
- https://signpath.org/terms.html
- https://signpath.org/apply.html
- https://docs.signpath.io/trusted-build-systems/github
- https://docs.github.com/en/billing/concepts/product-billing/github-actions
- https://opensource.org/license/apache-2-0

