# PluginHost

Owns worker process lifecycle and translation between the Protobuf wire protocol and Core ports.

It may depend on Contracts and Core. Workers and shells may depend on PluginHost; PluginHost may not depend on either.

