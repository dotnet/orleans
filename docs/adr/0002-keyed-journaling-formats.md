# Keyed journaling formats

Orleans Journaling supports registering multiple codec families at the same time by key. A codec family key selects the physical log format, durable operation codecs, and value codecs together. Storage providers expose the selected log format key per grain, and `LogManager` resolves the matching keyed services before recovery reads persisted bytes.

For a given grain, all runtime and user durable state machines share the storage provider's selected log format key. Durable state machine services remain unkeyed within the grain; they resolve their durable operation and value codec providers using the grain's active log format key. This avoids repeated per-service key selection while still allowing applications to host multiple journaling formats in one silo.

Storage provider options expose a default log format key plus an optional selector which receives only the grain type, for example `Func<GrainType, string>`. The selector does not receive the full `IGrainContext` or `GrainId`, keeping format choice at the grain-type/configuration level rather than making it depend on individual grain identity. Storage persists raw encoded bytes and does not persist the key; existing data must continue to be read with the same configured key.

The default Orleans binary codec uses the `orleans-binary` key internally. Additional codec packages own their format keys and can expose a well-known key, such as `JsonJournalingExtensions.LogFormatKey`, or keep it internal.

The key belongs to the grain's storage configuration, not to each durable collection injection site. This avoids silent mixing of physical log formats and durable operation codecs while preserving the cohesive codec family model.
