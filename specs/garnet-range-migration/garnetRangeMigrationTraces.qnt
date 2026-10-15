// The simulation configuration with the implementation's recent-ETag count,
// for exporting traces which the .NET conformance harness replays. It lives in
// its own file so that it adds nothing to the IR which TLC's translation of
// garnetRangeMigration.qnt sends to Apalache.
module garnetRangeMigrationTraces {
  import garnetRangeMigration(
    PARTITIONS = Set(1, 2, 3),
    KEYS = Set("a", "b", "c"),
    KEY_BUCKET = Map("a" -> 0, "b" -> 0, "c" -> 1),
    MAX_VIEWS = 5,
    MAX_OPS = 5,
    MAX_PULLS = 2,
    MAX_OPENS = 2,
    MAX_DONES = 2,
    MAX_RECLAIMS = 2,
    RECENT = 8,
    FILTER_CHOICES = true
  ).* from "./garnetRangeMigration"
}
