using System.Globalization;

namespace Orleans.Persistence.Cosmos.TypeInfo
{
    internal sealed class GrainStateTypeInfo
    {
        private readonly Func<string, GrainId, IGrainState, Task> readStateFunc;
        private readonly Func<string, GrainId, IGrainState, Task> writeStateFunc;
        private readonly Func<string, GrainId, IGrainState, Task> clearStateFunc;

        public GrainStateTypeInfo(
            string grainTypeName,
            Func<GrainReference, string> grainKeyFormatter,
            Func<string, GrainId, IGrainState, Task> readStateFunc,
            Func<string, GrainId, IGrainState, Task> writeStateFunc,
            Func<string, GrainId, IGrainState, Task> clearStateFunc)
        {
            this.readStateFunc = readStateFunc;
            this.writeStateFunc = writeStateFunc;
            this.clearStateFunc = clearStateFunc;
            this.GrainTypeName = grainTypeName;
            this.GrainKeyFormatter = grainKeyFormatter;
        }

        public string GrainTypeName { get; }

        public Func<GrainReference, string> GrainKeyFormatter { get; }

        public GrainId GetGrainId(GrainReference grainReference) => new(this.GrainTypeName, this.GrainKeyFormatter(grainReference));

        public Task ReadStateAsync(string stateName, GrainReference grainReference, IGrainState grainState) => this.readStateFunc(stateName, this.GetGrainId(grainReference), grainState);

        public Task WriteStateAsync(string stateName, GrainReference grainReference, IGrainState grainState) => this.writeStateFunc(stateName, this.GetGrainId(grainReference), grainState);

        public Task ClearStateAsync(string stateName, GrainReference grainReference, IGrainState grainState) => this.clearStateFunc(stateName, this.GetGrainId(grainReference), grainState);

        public static Func<GrainReference, string> GetGrainKeyFormatter(Type grainClass)
        {
            Func<GrainReference, string> grainKeyFormatter = null!;
            if (typeof(IGrainWithStringKey).IsAssignableFrom(grainClass))
            {
                grainKeyFormatter = static (grainReference) => grainReference.GetPrimaryKeyString();
            }

            if (typeof(IGrainWithGuidKey).IsAssignableFrom(grainClass))
            {
                if (grainKeyFormatter is not null)
                {
                    ThrowMultipleKeyInterfaces(grainClass);
                }

                grainKeyFormatter = static (grainReference) => grainReference.GetPrimaryKey(out _).ToString("N");
            }

            if (typeof(IGrainWithIntegerKey).IsAssignableFrom(grainClass))
            {
                if (grainKeyFormatter is not null)
                {
                    ThrowMultipleKeyInterfaces(grainClass);
                }

                grainKeyFormatter = static (grainReference) => grainReference.GetPrimaryKeyLong(out _).ToString("X", CultureInfo.InvariantCulture);
            }

            if (typeof(IGrainWithGuidCompoundKey).IsAssignableFrom(grainClass))
            {
                if (grainKeyFormatter is not null)
                {
                    ThrowMultipleKeyInterfaces(grainClass);
                }

                grainKeyFormatter = static (grainReference) =>
                {
                    var pk = grainReference.GetPrimaryKey(out var ext).ToString("N");
                    return $"{pk}+{ext}";
                };
            }

            if (typeof(IGrainWithIntegerCompoundKey).IsAssignableFrom(grainClass))
            {
                if (grainKeyFormatter is not null)
                {
                    ThrowMultipleKeyInterfaces(grainClass);
                }

                grainKeyFormatter = static (grainReference) =>
                {
                    var pk = grainReference.GetPrimaryKeyLong(out var ext).ToString("X", CultureInfo.InvariantCulture);
                    return $"{pk}+{ext}";
                };
            }

            if (grainKeyFormatter is null)
            {
                throw new InvalidOperationException($"Grain class '{grainClass}' must inherit a grain key interface ({nameof(IGrainWithGuidKey)}, {nameof(IGrainWithIntegerKey)}, {nameof(IGrainWithStringKey)}, {nameof(IGrainWithGuidCompoundKey)}, or {nameof(IGrainWithIntegerCompoundKey)}).");
            }

            return grainKeyFormatter;

            static void ThrowMultipleKeyInterfaces(Type grainClass)
            {
                throw new InvalidOperationException($"Grain type '{grainClass}' inherits multiple grain key interfaces which is not supported by this provider.");
            }
        }
    }
}
