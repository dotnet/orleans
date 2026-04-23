#nullable enable
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model.Incremental
{
    internal sealed class SerializableTypeExtractionResult
    {
        private SerializableTypeExtractionResult(SerializableTypeModel? model, Diagnostic? diagnostic)
        {
            Model = model;
            Diagnostic = diagnostic;
        }

        public static SerializableTypeExtractionResult Empty { get; } = new(null, null);

        public SerializableTypeModel? Model { get; }

        public Diagnostic? Diagnostic { get; }

        public static SerializableTypeExtractionResult FromModel(SerializableTypeModel model) => new(model, null);

        public static SerializableTypeExtractionResult FromDiagnostic(Diagnostic diagnostic) => new(null, diagnostic);
    }
}
