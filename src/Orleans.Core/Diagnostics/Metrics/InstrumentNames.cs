namespace Orleans.Runtime;

internal static class InstrumentNames
{
    // Networking
    public const string NETWORKING_SOCKETS_CLOSED = "networking-sockets-closed";
    public const string NETWORKING_SOCKETS_OPENED = "networking-sockets-opened";

    // Messaging
    public const string MESSAGING_SENT_MESSAGES_SIZE = "messaging-sent-messages-size";
    public const string MESSAGING_RECEIVED_MESSAGES_SIZE = "messaging-received-messages-size";
    public const string MESSAGING_SENT_BYTES_HEADER = "messaging-sent-header-size";
    public const string MESSAGING_SENT_FAILED = "messaging-sent-failed";
    public const string MESSAGING_SENT_DROPPED = "messaging-sent-dropped";
    public const string MESSAGING_RECEIVED_BYTES_HEADER = "messaging-received-header-size";

    public const string MESSAGING_DISPATCHER_RECEIVED = "messaging-processing-dispatcher-received";
    public const string MESSAGING_DISPATCHER_PROCESSED = "messaging-processing-dispatcher-processed";
    public const string MESSAGING_DISPATCHER_FORWARDED = "messaging-processing-dispatcher-forwarded";
    public const string MESSAGING_IMA_RECEIVED = "messaging-processing-ima-received";
    public const string MESSAGING_IMA_ENQUEUED = "messaging-processing-ima-enqueued";
    public const string MESSAGING_PROCESSING_ACTIVATION_DATA_ALL = "messaging-processing-activation-data";
    public const string MESSAGING_PINGS_SENT = "messaging-pings-sent";
    public const string MESSAGING_PINGS_RECEIVED = "messaging-pings-received";
    public const string MESSAGING_PINGS_REPLYRECEIVED = "messaging-pings-reply-received";
    public const string MESSAGING_PINGS_REPLYMISSED = "messaging-pings-reply-missed";
    public const string MESSAGING_EXPIRED = "messaging-expired";
    public const string MESSAGING_REJECTED = "messaging-rejected";
    public const string MESSAGING_REROUTED = "messaging-rerouted";
    public const string MESSAGING_SENT_LOCALMESSAGES = "messaging-sent-local";

    // Gateway
    public const string GATEWAY_CONNECTED_CLIENTS = "gateway-connected-clients";
    public const string GATEWAY_SENT = "gateway-sent";
    public const string GATEWAY_RECEIVED = "gateway-received";
    public const string GATEWAY_LOAD_SHEDDING = "gateway-load-shedding";

    // Runtime
    public const string SCHEDULER_NUM_LONG_RUNNING_TURNS = "scheduler-long-running-turns";

    // Catalog
    public const string CATALOG_ACTIVATION_COUNT = "activation-count";
    public const string CATALOG_ACTIVATION_WORKING_SET = "catalog-activation-working-set";
    public const string CATALOG_ACTIVATION_CREATED = "catalog-activation-created";
    public const string CATALOG_ACTIVATION_DESTROYED = "catalog-activation-destroyed";
    public const string CATALOG_ACTIVATION_FAILED_TO_ACTIVATE = "catalog-activation-failed-to-activate";
    public const string CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS = "catalog-activation-collections";
    public const string CATALOG_ACTIVATION_SHUTDOWN = "catalog-activation-shutdown";
    public const string CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS = "catalog-activation-non-existent";
    public const string CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS = "catalog-activation-concurrent-registration-attempts";

    // Directory
    // not used...
    public const string DIRECTORY_LOOKUPS_LOCAL_ISSUED = "directory-lookups-local-issued";
    // not used...
    public const string DIRECTORY_LOOKUPS_LOCAL_SUCCESSES = "directory-lookups-local-successes";
    public const string DIRECTORY_LOOKUPS_FULL_ISSUED = "directory-lookups-full-issued";
    public const string DIRECTORY_LOOKUPS_REMOTE_SENT = "directory-lookups-remote-sent";
    public const string DIRECTORY_LOOKUPS_REMOTE_RECEIVED = "directory-lookups-remote-received";
    public const string DIRECTORY_LOOKUPS_LOCALDIRECTORY_ISSUED = "directory-lookups-local-directory-issued";
    public const string DIRECTORY_LOOKUPS_LOCALDIRECTORY_SUCCESSES = "directory-lookups-local-directory-successes";
    // not used
    public const string DIRECTORY_LOOKUPS_CACHE_ISSUED = "directory-lookups-cache-issued";
    // not used
    public const string DIRECTORY_LOOKUPS_CACHE_SUCCESSES = "directory-lookups-cache-successes";
    public const string DIRECTORY_VALIDATIONS_CACHE_SENT = "directory-validations-cache-sent";
    public const string DIRECTORY_VALIDATIONS_CACHE_RECEIVED = "directory-validations-cache-received";
    public const string DIRECTORY_PARTITION_SIZE = "directory-partition-size";
    public const string DIRECTORY_CACHE_SIZE = "directory-cache-size";
    public const string DIRECTORY_RING_RINGSIZE = "directory-ring-size";
    public const string DIRECTORY_RING_MYPORTION_RINGDISTANCE = "directory-ring-local-portion-distance";
    public const string DIRECTORY_RING_MYPORTION_RINGPERCENTAGE = "directory-ring-local-portion-percentage";
    public const string DIRECTORY_RING_MYPORTION_AVERAGERINGPERCENTAGE = "directory-ring-local-portion-average-percentage";
    public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_ISSUED = "directory-registrations-single-act-issued";
    public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_LOCAL = "directory-registrations-single-act-local";
    public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_SENT = "directory-registrations-single-act-remote-sent";
    public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_RECEIVED = "directory-registrations-single-act-remote-received";
    public const string DIRECTORY_UNREGISTRATIONS_ISSUED = "directory-unregistrations-issued";
    public const string DIRECTORY_UNREGISTRATIONS_LOCAL = "directory-unregistrations-local";
    public const string DIRECTORY_UNREGISTRATIONS_REMOTE_SENT = "directory-unregistrations-remote-sent";
    public const string DIRECTORY_UNREGISTRATIONS_REMOTE_RECEIVED = "directory-unregistrations-remote-received";
    public const string DIRECTORY_UNREGISTRATIONS_MANY_ISSUED = "directory-unregistrations-many-issued";
    public const string DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_SENT = "directory-unregistrations-many-remote-sent";
    public const string DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_RECEIVED = "directory-unregistrations-many-remote-received";

    // ConsistentRing
    public const string CONSISTENTRING_SIZE = "consistent-ring-size";
    public const string CONSISTENTRING_LOCAL_SIZE_PERCENTAGE = "consistent-ring-range-percentage-local";
    public const string CONSISTENTRING_AVERAGE_SIZE_PERCENTAGE = "consistent-ring-range-percentage-average";

    // Watchdog
    public const string WATCHDOG_NUM_HEALTH_CHECKS = "watchdog-health-checks";
    public const string WATCHDOG_NUM_FAILED_HEALTH_CHECKS = "watchdog-health-checks-failed";

    // Client
    public const string CLIENT_CONNECTED_GATEWAY_COUNT = "client-connected-gateways";

    // Misc
    public const string GRAIN_COUNTS = "grains";
    public const string SYSTEM_TARGET_COUNTS = "system-targets";

    // App requests
    public const string REQUESTS_COMPLETED = "app-requests";
    public const string APP_REQUESTS_LATENCY_HISTOGRAM = "app-requests-latency";
    public const string APP_REQUESTS_TIMED_OUT = "app-requests-timedout";

    // Reminders
    public const string REMINDERS_TARDINESS = "reminders-tardiness";
    public const string REMINDERS_NUMBER_ACTIVE_REMINDERS = "reminders-active";
    public const string REMINDERS_COUNTERS_TICKS_DELIVERED = "reminders-ticks-delivered";

    // Storage
    public const string STORAGE_READ_ERRORS = "storage-read-errors";
    public const string STORAGE_WRITE_ERRORS = "storage-write-errors";
    public const string STORAGE_CLEAR_ERRORS = "storage-clear-errors";
    public const string STORAGE_READ_LATENCY = "storage-read-latency";
    public const string STORAGE_WRITE_LATENCY = "storage-write-latency";
    public const string STORAGE_CLEAR_LATENCY = "storage-clear-latency";

    // Streams
    public const string STREAMS_PUBSUB_PRODUCERS_ADDED = "streams-pubsub-producers-added";
    public const string STREAMS_PUBSUB_PRODUCERS_REMOVED = "streams-pubsub-producers-removed";
    public const string STREAMS_PUBSUB_PRODUCERS_TOTAL = "streams-pubsub-producers";
    public const string STREAMS_PUBSUB_CONSUMERS_ADDED = "streams-pubsub-consumers-added";
    public const string STREAMS_PUBSUB_CONSUMERS_REMOVED = "streams-pubsub-consumers-removed";
    public const string STREAMS_PUBSUB_CONSUMERS_TOTAL = "streams-pubsub-consumers";

    public const string STREAMS_PERSISTENT_STREAM_NUM_PULLING_AGENTS = "streams-persistent-stream-pulling-agents";
    public const string STREAMS_PERSISTENT_STREAM_NUM_READ_MESSAGES = "streams-persistent-stream-messages-read";
    public const string STREAMS_PERSISTENT_STREAM_NUM_SENT_MESSAGES = "streams-persistent-stream-messages-sent";
    public const string STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE = "streams-persistent-stream-pubsub-cache-size";

    public const string STREAMS_QUEUE_INITIALIZATION_FAILURES = "streams-queue-initialization-failures";
    public const string STREAMS_QUEUE_INITIALIZATION_DURATION = "streams-queue-initialization-duration";
    public const string STREAMS_QUEUE_INITIALIZATION_EXCEPTIONS = "streams-queue-initialization-exceptions";
    public const string STREAMS_QUEUE_READ_FAILURES = "streams-queue-read-failures";
    public const string STREAMS_QUEUE_READ_DURATION = "streams-queue-read-duration";
    public const string STREAMS_QUEUE_READ_EXCEPTIONS = "streams-queue-read-exceptions";
    public const string STREAMS_QUEUE_SHUTDOWN_FAILURES = "streams-queue-shutdown-failures";
    public const string STREAMS_QUEUE_SHUTDOWN_DURATION = "streams-queue-shutdown-duration";
    public const string STREAMS_QUEUE_SHUTDOWN_EXCEPTIONS = "streams-queue-shutdown-exceptions";
    public const string STREAMS_QUEUE_MESSAGES_RECEIVED = "streams-queue-messages-received";
    public const string STREAMS_QUEUE_OLDEST_MESSAGE_ENQUEUE_AGE = "streams-queue-oldest-message-enqueue-age";
    public const string STREAMS_QUEUE_NEWEST_MESSAGE_ENQUEUE_AGE = "streams-queue-newest-message-enqueue-age";

    public const string STREAMS_BLOCK_POOL_TOTAL_MEMORY = "streams-block-pool-total-memory";
    public const string STREAMS_BLOCK_POOL_AVAILABLE_MEMORY = "streams-block-pool-available-memory";
    public const string STREAMS_BLOCK_POOL_CLAIMED_MEMORY = "streams-block-pool-claimed-memory";
    public const string STREAMS_BLOCK_POOL_RELEASED_MEMORY = "streams-block-pool-released-memory";
    public const string STREAMS_BLOCK_POOL_ALLOCATED_MEMORY = "streams-block-pool-allocated-memory";

    public const string STREAMS_QUEUE_CACHE_SIZE = "streams-queue-cache-size";
    public const string STREAMS_QUEUE_CACHE_LENGTH = "streams-queue-cache-length";
    public const string STREAMS_QUEUE_CACHE_MESSAGES_ADDED = "streams-queue-cache-messages-added";
    public const string STREAMS_QUEUE_CACHE_MESSAGES_PURGED = "streams-queue-cache-messages-purged";
    public const string STREAMS_QUEUE_CACHE_MEMORY_ALLOCATED = "streams-queue-cache-memory-allocated";
    public const string STREAMS_QUEUE_CACHE_MEMORY_RELEASED = "streams-queue-cache-memory-released";
    public const string STREAMS_QUEUE_CACHE_OLDEST_TO_NEWEST_DURATION = "streams-queue-cache-oldest-to-newest-duration";
    public const string STREAMS_QUEUE_CACHE_OLDEST_AGE = "streams-queue-cache-oldest-age";
    public const string STREAMS_QUEUE_CACHE_PRESSURE = "streams-queue-cache-pressure";
    public const string STREAMS_QUEUE_CACHE_UNDER_PRESSURE = "streams-queue-cache-under-pressure";
    public const string STREAMS_QUEUE_CACHE_PRESSURE_CONTRIBUTION_COUNT = "streams-queue-cache-pressure-contribution-count";

    public const string RUNTIME_MEMORY_TOTAL_PHYSICAL_MEMORY_MB = "runtime-total-physical-memory";
    public const string RUNTIME_MEMORY_AVAILABLE_MEMORY_MB = "runtime-available-memory";
}
