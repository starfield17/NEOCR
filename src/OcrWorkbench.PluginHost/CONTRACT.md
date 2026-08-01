# PluginHost

Owns worker process lifecycle and translation between the Protobuf wire protocol and Core ports.

Recognizer sessions may reuse one healthy single-flight worker. Cancellation must drain both the cancel acknowledgement and the target response before reuse; an unresponsive or protocol-faulted worker is terminated and may be replaced only for a later request.

It may depend on Contracts and Core. Workers and shells may depend on PluginHost; PluginHost may not depend on either.
