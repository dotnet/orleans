using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class InstrumentNames
{
    // Networking
    public const string NETWORKING_SOCKETS_CLOSED = "orleans-networking-sockets-closed";
    public const string NETWORKING_SOCKETS_OPENED = "orleans-networking-sockets-opened";

    // Messaging
    public const string MESSAGING_SENT_MESSAGES_SIZE = "orleans-messaging-sent-messages-size";
    public const string MESSAGING_RECEIVED_MESSAGES_SIZE = "orleans-messaging-received-messages-size";
    public const string MESSAGING_SENT_BYTES_HEADER = "orleans-messaging-sent-bytes-header-size";
    public const string MESSAGING_SENT_FAILED = "orleans-messaging-sent-failed";
    public const string MESSAGING_SENT_DROPPED = "orleans-messaging-sent-dropped";
    public const string MESSAGING_RECEIVED_BYTES_HEADER = "orleans-messaging-received-header-size";

    public const string MESSAGING_DISPATCHER_RECEIVED = "orleans-messaging-processing-dispatcher-received";
    public const string MESSAGING_DISPATCHER_PROCESSED = "orleans-messaging-processing-dispatcher-processed";
    public const string MESSAGING_IMA_RECEIVED = "orleans-messaging-processing-ima-received";
    public const string MESSAGING_IMA_ENQUEUED = "orleans-messaging-processing-ima-enqueued";
    public const string MESSAGING_DISPATCHER_FORWARDED = "orleans-messaging-processing-dispatcher-forwarded";
    public const string MESSAGING_PROCESSING_ACTIVATION_DATA_ALL = "orleans-messaging-processing-activation-data";
    public const string MESSAGING_PINGS_SENT = "orleans-messaging-pings-sent";
    public const string MESSAGING_PINGS_RECEIVED = "orleans-messaging-pings-received";
    public const string MESSAGING_PINGS_REPLYRECEIVED = "orleans-messaging-pings-reply-received";
    public const string MESSAGING_PINGS_REPLYMISSED = "orleans-messaging-pings-reply-missed";
    public const string MESSAGING_EXPIRED = "orleans-messaging-expired";
    public const string MESSAGING_REJECTED = "orleans-messaging-rejected";
    public const string MESSAGING_REROUTED = "orleans-messaging-rerouted";
    public const string MESSAGING_SENT_LOCALMESSAGES = "orleans-messaging-sent-local-messages";

    // Gateway
    public const string GATEWAY_CONNECTED_CLIENTS = "orleans-gateway-connected-clients";
    public const string GATEWAY_SENT = "orleans-gateway-sent";
    public const string GATEWAY_RECEIVED = "orleans-gateway-received";
    public const string GATEWAY_LOAD_SHEDDING = "orleans-gateway-load-shedding";

    // Runtime
    // not used
    public const string SCHEDULER_NUM_LONG_RUNNING_TURNS = "orleans-scheduler-long-running-turns";

    // Catalog
    public const string CATALOG_ACTIVATION_COUNT = "orleans-catalog-activations";
    public const string CATALOG_ACTIVATION_CREATED = "orleans-catalog-activation-created";
    public const string CATALOG_ACTIVATION_DESTROYED = "orleans-catalog-activation-destroyed";
    public const string CATALOG_ACTIVATION_FAILED_TO_ACTIVATE = "orleans-catalog-activation-failed-to-activate";
    public const string CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS = "orleans-catalog-activation-collections";
    public const string CATALOG_ACTIVATION_SHUTDOWN = "orleans-catalog-activation-shutdown";
    public const string CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS = "orleans-catalog-activation-non-existent";
    public const string CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS = "orleans-catalog-activation-concurrent-registration-attempts";

    // Directory
    public const string DIRECTORY_LOOKUPS_LOCAL_ISSUED = "orleans-directory-lookups-local-issued";
    public const string DIRECTORY_LOOKUPS_LOCAL_SUCCESSES = "orleans-directory-lookups-local-successes";
    public const string DIRECTORY_LOOKUPS_FULL_ISSUED = "orleans-directory-lookups-full-issued";
    public const string DIRECTORY_LOOKUPS_REMOTE_SENT = "orleans-directory-lookups-remote-sent";
    public const string DIRECTORY_LOOKUPS_REMOTE_RECEIVED = "orleans-directory-lookups-remote-received";
    public const string DIRECTORY_LOOKUPS_LOCALDIRECTORY_ISSUED = "orleans-directory-lookups-local-directory-issued";
    public const string DIRECTORY_LOOKUPS_LOCALDIRECTORY_SUCCESSES = "orleans-directory-lookups-local-directory-successes";
    public const string DIRECTORY_LOOKUPS_CACHE_ISSUED = "orleans-directory-lookups-cache-issued";
    public const string DIRECTORY_LOOKUPS_CACHE_SUCCESSES = "orleans-directory-lookups-cache-successes";
    // TODO: provide query for this
    public const string DIRECTORY_LOOKUPS_CACHE_HITRATIO = "orleans-directory-lookups-cache-hit-ratio";
    public const string DIRECTORY_VALIDATIONS_CACHE_SENT = "orleans-directory-validations-cache-sent";
    public const string DIRECTORY_VALIDATIONS_CACHE_RECEIVED = "orleans-directory-validations-cache-received";
    public const string DIRECTORY_PARTITION_SIZE = "orleans-directory-partition-size";
    public const string DIRECTORY_CACHE_SIZE = "orleans-directory-cache-size";
    public const string DIRECTORY_RING_RINGSIZE = "orleans-directory-ring-size";
    public const string DIRECTORY_RING_MYPORTION_RINGDISTANCE = "orleans-directory-ring-my-portion-distance";
    public const string DIRECTORY_RING_MYPORTION_RINGPERCENTAGE = "orleans-directory-ring-my-portion-percentage";
    public const string DIRECTORY_RING_MYPORTION_AVERAGERINGPERCENTAGE = "orleans-directory-ring-my-portion-average-percentage";
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
    public const string CONSISTENTRING_RINGSIZE = "orleans-consistent-ring-size";
    public const string CONSISTENTRING_MYRANGE_RINGPERCENTAGE = "orleans-consistent-ring-range-percentage-my";
    public const string CONSISTENTRING_AVERAGERINGPERCENTAGE = "orleans-consistent-ring-range-percentage-average";

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
}
