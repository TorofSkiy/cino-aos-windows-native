# Data and system behavior

The portable core contains no advertising or automatic vendor analytics endpoint. It does not send data to a configured management server until an operator supplies that connection and enrollment configuration.

Local storage:
- Workbench: prompts, generated/edited text, filenames in inventory results, task history and checksums under the selected workspace.
- Host: generated device identity, credentials, request sequences, tasks and receipts in its explicitly selected state directory.
- Local inference: prompts sent to a loopback engine started by the workbench. The model runtime may have its own terms; it is not bundled.
- Diagnostics: an explicit user export contains operating-system, host and runtime observations. Inspect before sharing.

When network enrollment is explicitly configured, the management server receives device identity, machine name, OS/resource information, heartbeats, capability reports and results of authorized typed tasks. Model-task prompts and returned text may then cross the configured connection. Enrollment and management require HTTPS with certificate validation outside explicit loopback tests.

The workbench does not automatically execute model-generated commands. Desktop tasks run in the user's session. The portable preview installs no kernel driver, service, startup hook, scheduled task, certificate or browser extension. It does not modify Windows activation or system security policy.

The developer build scripts contact NuGet to restore dependencies. A manually run GitHub workflow sends source/build artifacts to GitHub; a future SignPath submission would disclose selected artifacts and build metadata to SignPath only after enrollment and policy approval. This candidate has no signing token or submission step.

Retention and deletion: data remain on the user's machine until explicitly removed; artifacts may include sensitive content. Closing/deleting the portable application does not erase user data. Maintainers must publish an actual security-reporting channel before public distribution.

