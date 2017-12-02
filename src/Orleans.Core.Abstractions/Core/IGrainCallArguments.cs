using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans
{
    public interface IGrainCallArgumentVisitor<TContext>
    {
        void Visit<TArg>(ref TArg item, TContext context);
    }

    public interface IGrainCallArguments : IReadOnlyList<object>
    {
        int Length { get; }

        new object this[int index] { get; set; }

        void Visit<TContext>(IGrainCallArgumentVisitor<TContext> vistor, TContext context);
    }
}
