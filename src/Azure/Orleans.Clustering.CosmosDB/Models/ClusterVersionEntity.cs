namespace Orleans.Clustering.CosmosDB;

internal class ClusterVersionEntity : BaseClusterEntity
{
    public override string EntityType => nameof(ClusterVersionEntity);

    [JsonPropertyName(nameof(ClusterVersion))]
    public int ClusterVersion { get; set; } = 0;

    // public static ClusterVersionEntity FromJson(ref Utf8JsonReader reader)
    // {
    //     var entity = new ClusterVersionEntity();

    //     while (reader.Read())
    //     {
    //         if (reader.TokenType == JsonTokenType.EndObject)
    //         {
    //             break;
    //         }
    //         else if (reader.TokenType == JsonTokenType.PropertyName)
    //         {
    //             var propertyName = reader.GetString();
    //             switch (propertyName)
    //             {
    //                 case ID_FIELD:
    //                     entity.Id = reader.GetString();
    //                     break;
    //                 case ETAG_FIELD:
    //                     entity.Id = reader.GetString();
    //                     break;
    //                 case nameof(ClusterId):
    //                     entity.ClusterId = reader.GetString();
    //                     break;
    //                 case nameof(ClusterVersion):
    //                     entity.ClusterVersion = reader.GetInt32();
    //                     break;
    //                 default:
    //                     reader.Skip();
    //                     break;
    //             }
    //         }
    //     }

    //     return entity;
    // }
}