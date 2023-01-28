namespace Orleans.Runtime;

internal static class InstrumentNames
{
    // Networking
    public const string NETWORKING_SOCKETS_CLOSED = "orleans-networking-sockets-closed";
    public const string NETWORKING_SOCKETS_OPENED = "orleans-networking-sockets-opened";

    // Messaging
    public const string MESSAGING_SENT_MESSAGES_SIZE = "orleans-messaging-sent-messages-size";
    public const string MESSAGING_RECEIVED_MESSAGES_SIZE = "orleans-messaging-received-messages-size";
    public const string MESSAGING_SENT_BYTES_HEADER = "orleans-messaging-sent-header-size";
    public const string MESSAGING_SENT_FAILED = "orleans-messaging-sent-failed";
    public const string MESSAGING_SENT_DROPPED = "orleans-messaging-sent-dropped";
    public const string MESSAGING_RECEIVED_BYTES_HEADER = "orleans-messaging-received-header-size";

    public const string MESSAGING_DISPATCHER_RECEIVED = "orleans-messaging-processing-dispatcher-received";
    public const string MESSAGING_DISPATCHER_PROCESSED = "orleans-messaging-processing-dispatcher-processed";
    public const string MESSAGING_DISPATCHER_FORWARDED = "orleans-messaging-processing-dispatcher-forwarded";
    public const string MESSAGING_IMA_RECEIVED = "orleans-messaging-processing-ima-received";
    public const string MESSAGING_IMA_ENQUEUED = "orleans-messaging-processing-ima-enqueued";
    public const string MESSAGING_PROCESSING_ACTIVATION_DATA_ALL = "orleans-messaging-processing-activation-data";
    public const string MESSAGING_PINGS_SENT = "orleans-messaging-pings-sent";
    public const string MESSAGING_PINGS_RECEIVED = "orleans-messaging-pings-received";
    public const string MESSAGING_PINGS_REPLYRECEIVED = "orleans-messaging-pings-reply-received";
    public const string MESSAGING_PINGS_REPLYMISSED = "orleans-messaging-pings-reply-missed";
    public const string MESSAGING_EXPIRED = "orleans-messaging-expired";
    public const string MESSAGING_REJECTED = "orleans-messaging-rejected";
    public const string MESSAGING_REROUTED = "orleans-messaging-rerouted";
    public const string MESSAGING_SENT_LOCALMESSAGES = "orleans-messaging-sent-local";

    // Gateway
    public const string GATEWAY_CONNECTED_CLIENTS = "orleans-gateway-connected-clients";
    public const string GATEWAY_SENT = "orleans-gateway-sent";
    public const string GATEWAY_RECEIVED = "orleans-gateway-received";
    public const string GATEWAY_LOAD_SHEDDING = "orleans-gateway-load-shedding";

    // Runtime
    public const string SCHEDULER_NUM_LONG_RUNNING_TURNS = "orleans-scheduler-long-running-turns";

    // Catalog
    public const string CATALOG_ACTIVATION_COUNT = "orleans-catalog-activations";
    public const string CATALOG_ACTIVATION_WORKING_SET = "orleans-catalog-activation-working-set";
    public const string CATALOG_ACTIVATION_CREATED = "orleans-catalog-activation-created";
    public const string CATALOG_ACTIVATION_DESTROYED = "orleans-catalog-activation-destroyed";
    public const string CATALOG_ACTIVATION_FAILED_TO_ACTIVATE = "orleans-catalog-activation-failed-to-activate";
    public const string CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS = "orleans-catalog-activation-collections";
    public const string CATALOG_ACTIVATION_SHUTDOWN = "orleans-catalog-activation-shutdown";
    public const string CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS = "orleans-catalog-activation-non-existent";
    public const string CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS = "orleans-catalog-activation-concurrent-registration-attempts";

    // Directory
    // not used...
    public const string DIRECTORY_LOOKUPS_LOCAL_ISSUED = "orleans-directory-lookups-local-issued";
    // not used...
    public const string DIRECTORY_LOOKUPS_LOCAL_SUCCESSES = "orleans-directory-lookups-local-successes";
    public const string DIRECTORY_LOOKUPS_FULL_ISSUED = "orleans-directory-lookups-full-issued";
    public const string DIRECTORY_LOOKUPS_REMOTE_SENT = "orleans-directory-lookups-remote-sent";
    public const string DIRECTORY_LOOKUPS_REMOTE_RECEIVED = "orleans-directory-lookups-remote-received";
    public const string DIRECTORY_LOOKUPS_LOCALDIRECTORY_ISSUED = "orleans-directory-lookups-local-directory-issued";
    public const string DIRECTORY_LOOKUPS_LOCALDIRECTORY_SUCCESSES = "orleans-directory-lookups-local-directory-successes";
    // not used
    public const string DIRECTORY_LOOKUPS_CACHE_ISSUED = "orleans-directory-lookups-cache-issued";
    // not used
    public const string DIRECTORY_LOOKUPS_CACHE_SUCCESSES = "orleans-directory-lookups-cache-successes";
    public const string DIRECTORY_VALIDATIONS_CACHE_SENT = "orleans-directory-validations-cache-sent";
    public const string DIRECTORY_VALIDATIONS_CACHE_RECEIVED = "orleans-directory-validations-cache-received";
    public const string DIRECTORY_PARTITION_SIZE = "orleans-directory-partition-size";
    public const string DIRECTORY_CACHE_SIZE = "orleans-directory-cache-size";
    public const string DIRECTORY_RING_RINGSIZE = "orleans-directory-ring-size";
    public const string DIRECTORY_RING_MYPORTION_RINGDISTANCE = "orleans-directory-ring-local-portion-distance";
    public const string DIRECTORY_RING_MYPORTION_RINGPERCENTAGE = "orleans-directory-ring-local-portion-percentage";
    public const string DIRECTORY_RING_MYPORTION_AVERAGERINGPERCENTAGE = "orleans-directory-ring-local-portion-average-percentage";
    public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_ISSUED = "orleans-directory-registrations-single-act-issued";
    public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_LOCAL = "orleans-directory-registrations-single-act-local";
    public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_SENT = "orleans-directory-registrations-single-act-remote-sent";
    public const string DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_RECEIVED = "orleans-directory-registrations-single-act-remote-received";
    public const string DIRECTORY_UNREGISTRATIONS_ISSUED = "orleans-directory-unregistrations-issued";
    public const string DIRECTORY_UNREGISTRATIONS_LOCAL = "orleans-directory-unregistrations-local";
    public const string DIRECTORY_UNREGISTRATIONS_REMOTE_SENT = "orleans-directory-unregistrations-remote-sent";
    public const string DIRECTORY_UNREGISTRATIONS_REMOTE_RECEIVED = "orleans-directory-unregistrations-remote-received";
    public const string DIRECTORY_UNREGISTRATIONS_MANY_ISSUED = "orleans-directory-unregistrations-many-issued";
    public const string DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_SENT = "orleans-directory-unregistrations-many-remote-sent";
    public const string DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_RECEIVED = "orleans-directory-unregistrations-many-remote-received";

    // ConsistentRing
    public const string CONSISTENTRING_SIZE = "orleans-consistent-ring-size";
    public const string CONSISTENTRING_LOCAL_SIZE_PERCENTAGE = "orleans-consistent-ring-range-percentage-local";
    public const string CONSISTENTRING_AVERAGE_SIZE_PERCENTAGE = "orleans-consistent-ring-range-percentage-average";

    // Watchdog
    public const string WATCHDOG_NUM_HEALTH_CHECKS = "orleans-watchdog-health-checks";
    public const string WATCHDOG_NUM_FAILED_HEALTH_CHECKS = "orleans-watchdog-health-checks-failed";

    // Client
    public const string CLIENT_CONNECTED_GATEWAY_COUNT = "orleans-client-connected-gateways";

    // Misc
    public const string GRAIN_COUNTS = "orleans-grains";
    public const string SYSTEM_TARGET_COUNTS = "orleans-system-targets";

    // App requests
    public const string APP_REQUESTS_LATENCY_HISTOGRAM = "orleans-app-requests-latency";
    public const string APP_REQUESTS_TIMED_OUT = "orleans-app-requests-timedout";

    // Reminders
    public const string REMINDERS_TARDINESS = "orleans-reminders-tardiness";
    public const string REMINDERS_NUMBER_ACTIVE_REMINDERS = "orleans-reminders-active";
    public const string REMINDERS_COUNTERS_TICKS_DELIVERED = "orleans-reminders-ticks-delivered";

    // Storage
    public const string STORAGE_READ_ERRORS = "orleans-storage-read-errors";
    public const string STORAGE_WRITE_ERRORS = "orleans-storage-write-errors";
    public const string STORAGE_CLEAR_ERRORS = "orleans-storage-clear-errors";
    public const string STORAGE_READ_LATENCY = "orleans-storage-read-latency";
    public const string STORAGE_WRITE_LATENCY = "orleans-storage-write-latency";
    public const string STORAGE_CLEAR_LATENCY = "orleans-storage-clear-latency";

    // Streams
    public const string STREAMS_PUBSUB_PRODUCERS_ADDED = "orleans-streams-pubsub-producers-added";
    public const string STREAMS_PUBSUB_PRODUCERS_REMOVED = "orleans-streams-pubsub-producers-removed";
    public const string STREAMS_PUBSUB_PRODUCERS_TOTAL = "orleans-streams-pubsub-producers";
    public const string STREAMS_PUBSUB_CONSUMERS_ADDED = "orleans-streams-pubsub-consumers-added";
    public const string STREAMS_PUBSUB_CONSUMERS_REMOVED = "orleans-streams-pubsub-consumers-removed";
    public const string STREAMS_PUBSUB_CONSUMERS_TOTAL = "orleans-streams-pubsub-consumers";

    public const string STREAMS_PERSISTENT_STREAM_NUM_PULLING_AGENTS = "orleans-streams-persistent-stream-pulling-agents";
    public const string STREAMS_PERSISTENT_STREAM_NUM_READ_MESSAGES = "orleans-streams-persistent-stream-messages-read";
    public const string STREAMS_PERSISTENT_STREAM_NUM_SENT_MESSAGES = "orleans-streams-persistent-stream-messages-sent";
    public const string STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE = "orleans-streams-persistent-stream-pubsub-cache-size";

    public const string STREAMS_QUEUE_INITIALIZATION_FAILURES = "orleans-streams-queue-initialization-failures";
    public const string STREAMS_QUEUE_INITIALIZATION_DURATION = "orleans-streams-queue-initialization-duration";
    public const string STREAMS_QUEUE_INITIALIZATION_EXCEPTIONS = "orleans-streams-queue-initialization-exceptions";
    public const string STREAMS_QUEUE_READ_FAILURES = "orleans-streams-queue-read-failures";
    public const string STREAMS_QUEUE_READ_DURATION = "orleans-streams-queue-read-duration";
    public const string STREAMS_QUEUE_READ_EXCEPTIONS = "orleans-streams-queue-read-exceptions";
    public const string STREAMS_QUEUE_SHUTDOWN_FAILURES = "orleans-streams-queue-shutdown-failures";
    public const string STREAMS_QUEUE_SHUTDOWN_DURATION = "orleans-streams-queue-shutdown-duration";
    public const string STREAMS_QUEUE_SHUTDOWN_EXCEPTIONS = "orleans-streams-queue-shutdown-exceptions";
    public const string STREAMS_QUEUE_MESSAGES_RECEIVED = "orleans-streams-queue-messages-received";
    public const string STREAMS_QUEUE_OLDEST_MESSAGE_ENQUEUE_AGE = "orleans-streams-queue-oldest-message-enqueue-age";
    public const string STREAMS_QUEUE_NEWEST_MESSAGE_ENQUEUE_AGE = "orleans-streams-queue-newest-message-enqueue-age";

    public const string STREAMS_BLOCK_POOL_TOTAL_MEMORY = "orleans-streams-block-pool-total-memory";
    public const string STREAMS_BLOCK_POOL_AVAILABLE_MEMORY = "orleans-streams-block-pool-available-memory";
    public const string STREAMS_BLOCK_POOL_CLAIMED_MEMORY = "orleans-streams-block-pool-claimed-memory";
    public const string STREAMS_BLOCK_POOL_RELEASED_MEMORY = "orleans-streams-block-pool-released-memory";
    public const string STREAMS_BLOCK_POOL_ALLOCATED_MEMORY = "orleans-streams-block-pool-allocated-memory";

    public const string STREAMS_QUEUE_CACHE_SIZE = "orleans-streams-queue-cache-size";
    public const string STREAMS_QUEUE_CACHE_LENGTH = "orleans-streams-queue-cache-length";
    public const string STREAMS_QUEUE_CACHE_MESSAGES_ADDED = "orleans-streams-queue-cache-messages-added";
    public const string STREAMS_QUEUE_CACHE_MESSAGES_PURGED = "orleans-streams-queue-cache-messages-purged";
    public const string STREAMS_QUEUE_CACHE_MEMORY_ALLOCATED = "orleans-streams-queue-cache-memory-allocated";
    public const string STREAMS_QUEUE_CACHE_MEMORY_RELEASED = "orleans-streams-queue-cache-memory-released";
    public const string STREAMS_QUEUE_CACHE_OLDEST_TO_NEWEST_DURATION = "orleans-streams-queue-cache-oldest-to-newest-duration";
    public const string STREAMS_QUEUE_CACHE_OLDEST_AGE = "orleans-streams-queue-cache-oldest-age";
    public const string STREAMS_QUEUE_CACHE_PRESSURE = "orleans-streams-queue-cache-pressure";
    public const string STREAMS_QUEUE_CACHE_UNDER_PRESSURE = "orleans-streams-queue-cache-under-pressure";
    public const string STREAMS_QUEUE_CACHE_PRESSURE_CONTRIBUTION_COUNT = "orleans-streams-queue-cache-pressure-contribution-count";

    public const string RUNTIME_MEMORY_TOTAL_PHSYSICAL_MEMORY_MB = "orleans-runtime-total-phsyical-memory";
    public const string RUNTIME_MEMORY_AVAILABLE_MEMORY_MB = "orleans-runtime-available-memory";
}
