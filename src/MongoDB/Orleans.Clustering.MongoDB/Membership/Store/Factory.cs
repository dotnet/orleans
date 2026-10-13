using MongoDB.Driver;
using Orleans.Clustering.MongoDB;
using Orleans.Providers.MongoDB.Membership.Store.Multiple;
using Orleans.Providers.MongoDB.Membership.Store.MultipleDeprecated;
using Orleans.Providers.MongoDB.Membership.Store.Single;
using System;

namespace Orleans.Providers.MongoDB.Membership.Store;

internal static class Factory
{
    public static IMongoMembershipCollection CreateCollection(IMongoClient mongoClient, MongoDBOptions options, MongoDBMembershipStrategy strategy)
    {
        switch (strategy)
        {
            case MongoDBMembershipStrategy.SingleDocument:
                return new SingleMembershipCollection(
                   mongoClient,
                   options.DatabaseName,
                   options.CollectionPrefix,
                   options.CreateShardKeyForCosmos,
                   options.CollectionConfigurator);
            case MongoDBMembershipStrategy.Multiple:
                return new MultipleMembershipCollection(
                    mongoClient,
                    options.DatabaseName,
                    options.CollectionPrefix,
                    options.CollectionConfigurator,
                    options.CreateShardKeyForCosmos);
            case MongoDBMembershipStrategy.MultipleDeprecated:
                return new MultipleDeprecatedMembershipCollection(
                    mongoClient,
                    options.DatabaseName,
                    options.CollectionPrefix,
                    options.CollectionConfigurator,
                    options.CreateShardKeyForCosmos);
        }

        throw new ArgumentException("Invalid strategy.", nameof(strategy));
    }
}
